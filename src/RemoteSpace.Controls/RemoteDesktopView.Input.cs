using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RemoteSpace.Core;
using RemoteSpace.Graphics;
using RemoteSpace.Input;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace RemoteSpace.Controls;

public sealed partial class RemoteDesktopView
{
    private readonly InputState _input = new();
    public long CoalescedPointerMoves => _outbox?.CoalescedMoves ?? 0;
    private void ConfigureInput()
    {
        KeyDown += (_, e) =>
        {
            if (_session?.State != SessionState.Active || IsScrollBarInput(e.OriginalSource)) return;
            if ((uint)e.Key == 0x13) { QueueMany(InputState.PauseKey()); e.Handled = true; return; }
#if __WASM__
            // Uno's browser KeyStatus is not implemented; its key mapping still supplies virtual keys.
            var key = _input.KeyDown((uint)e.Key);
#else
            var key = _input.KeyDown((uint)e.Key, e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey);
#endif
            if (key is { } value) { Queue(value); e.Handled = true; }
        };
        KeyUp += (_, e) => { if (IsScrollBarInput(e.OriginalSource)) return; if (_input.KeyUp((uint)e.Key) is { } value) { Queue(value); e.Handled = true; } };
        LostFocus += (_, _) => ReleaseHeldInput();
        PointerCaptureLost += (_, _) => ReleaseHeldInput();
        PointerPressed += (_, e) =>
        {
            if (_session?.State != SessionState.Active || IsScrollBarInput(e.OriginalSource)) return;
            var p = e.GetCurrentPoint(_canvas); var mapped = _canvas.Map(p.Position, false);
            if (mapped is not { } point) return;
            Focus(FocusState.Pointer); CapturePointer(e.Pointer);
            ushort button = p.Properties.PointerUpdateKind switch
            {
                Microsoft.UI.Input.PointerUpdateKind.LeftButtonPressed => 0x1000,
                Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed => 0x2000,
                Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed => 0x4000,
                _ => 0
            };
            if (button != 0 && _input.Button(button, true, point.X, point.Y) is { } value) Queue(value);
            ExtendedPointer(p.Properties.PointerUpdateKind, true, point.X, point.Y);
            e.Handled = true;
        };
        PointerReleased += (_, e) =>
        {
            if (IsScrollBarInput(e.OriginalSource)) return;
            var p = e.GetCurrentPoint(_canvas); var mapped = _canvas.Map(p.Position, true);
            if (mapped is { } point)
            {
                ushort button = p.Properties.PointerUpdateKind switch
                {
                    Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased => 0x1000,
                    Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased => 0x2000,
                    Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased => 0x4000,
                    _ => 0
                };
                if (button != 0 && _input.Button(button, false, point.X, point.Y) is { } value) Queue(value);
                ExtendedPointer(p.Properties.PointerUpdateKind, false, point.X, point.Y);
            }
            if (_input.HeldButtonCount == 0) ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        };
        PointerMoved += (_, e) =>
        {
            if (_session?.State != SessionState.Active || IsScrollBarInput(e.OriginalSource)) return;
            if (_canvas.Map(e.GetCurrentPoint(_canvas).Position, _input.HeldButtonCount > 0) is { } point)
            { _canvas.MoveCursor(point.X, point.Y); Queue(_input.Move(point.X, point.Y)); e.Handled = true; }
        };
        PointerWheelChanged += (_, e) =>
        {
            if (_session?.State != SessionState.Active || IsScrollBarInput(e.OriginalSource)) return;
            var p = e.GetCurrentPoint(_canvas);
            bool horizontal = p.Properties.IsHorizontalMouseWheel;
            if (horizontal && (_session as IRemoteInputCapabilities)?.InputCapabilities?.HorizontalWheel != true)
            { InputError?.Invoke("The remote server does not support horizontal scrolling."); e.Handled = true; return; }
            if (_canvas.Map(p.Position, false) is { } point)
            { QueueMany(_input.Wheel(Math.Clamp(p.Properties.MouseWheelDelta, -12000, 12000), point.X, point.Y, horizontal)); e.Handled = true; }
        };
    }
    private void ExtendedPointer(Microsoft.UI.Input.PointerUpdateKind kind, bool down, ushort x, ushort y)
    {
        ushort button = kind switch
        {
            Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed or Microsoft.UI.Input.PointerUpdateKind.XButton1Released => 1,
            Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed or Microsoft.UI.Input.PointerUpdateKind.XButton2Released => 2,
            _ => 0
        };
        if (button == 0) return;
        if ((_session as IRemoteInputCapabilities)?.InputCapabilities?.ExtendedMouse != true)
        { InputError?.Invoke("The remote server does not support mouse buttons 4 and 5."); return; }
        if (_input.ExtendedButton(button, down, x, y) is { } input) Queue(input);
    }
    public void ReleaseHeldInput() => QueueMany(_input.ReleaseAll());
    public async Task ReleaseHeldInputAsync(CancellationToken ct = default)
    {
        ReleaseHeldInput();
        if (_outbox is { } queue)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            await queue.FlushAsync(deadline.Token);
        }
    }
    public void SendSecureAttention() { ReleaseHeldInput(); QueueMany(InputState.SecureAttention()); }
    public void SendText(string text)
    {
        if (_session?.State != SessionState.Active) throw new InvalidOperationException("The remote desktop is not active.");
        if ((_session as IRemoteInputCapabilities)?.InputCapabilities?.Unicode != true) throw new NotSupportedException("Remote Unicode input was not negotiated.");
        var events = InputState.UnicodeText(text);
        // Reject the whole command before sending any characters, rather than filling
        // the queue half-way through the string and forcibly disconnecting the user.
        if (_outbox is null || !_outbox.TryEnqueue(events)) throw new InvalidOperationException("Text exceeds available input queue capacity; send a smaller selection.");
    }
    private void Queue(RemoteInput input) => QueueMany([input]);
    private void QueueMany(ReadOnlySpan<RemoteInput> inputs)
    {
        if (inputs.Length == 0 || _session is null || _outbox is null ||
            _session.State is not (SessionState.Active or SessionState.Establishing)) return;
        if (!_outbox.TryEnqueue(inputs))
        {
            _stop?.Cancel(); _ = _session.DisconnectAsync();
            InputError?.Invoke("Remote input queue exceeded its bound. The session was disconnected to avoid stuck keys.");
        }
    }
    private async Task WriteInputAsync(IRemoteSession session, RemoteInputQueue queue, CancellationToken ct)
    {
        var adjusted = new RemoteInput[RemoteInputValidation.MaximumBatch];
        try
        {
            await queue.RunAsync(async (inputs, token) =>
            {
                await SessionReadiness.WaitForActiveAsync(session, token);
                var size = _canvas.SizeSnapshot();
                for (int i=0;i<inputs.Length;i++)
                {
                    var input = inputs.Span[i];
                    if (RemoteInputValidation.IsPositioned(input) && size.Width > 0 && size.Height > 0)
                        input = input with { X = (ushort)Math.Min(input.X, size.Width - 1), Y = (ushort)Math.Min(input.Y, size.Height - 1) };
                    adjusted[i] = input;
                }
                if (session is IRemoteInputBatch batch) await batch.SendInputsAsync(adjusted.AsMemory(0, inputs.Length), token);
                else for (int i=0;i<inputs.Length;i++) await session.SendInputAsync(adjusted[i], token);
                Array.Clear(adjusted);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            DispatcherQueue.TryEnqueue(() => InputError?.Invoke(error.Message));
            await session.DisconnectAsync();
        }
        finally { Array.Clear(adjusted); }
    }
}

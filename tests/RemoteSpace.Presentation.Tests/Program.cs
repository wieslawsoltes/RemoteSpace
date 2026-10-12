using RemoteSpace.Graphics;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); checks++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { bool thrown = false; try { action(); } catch (ArgumentException) { thrown = true; } Check(thrown, name); }
using var gate = new PresentationGate();
Check(gate.Request() == 0, "inactive presentation records dirt without scheduling");
long first = gate.SetActive(true);
Check(first > 0 && gate.IsScheduled(first), "activation schedules retained dirty state");
int scheduled = 0;
Parallel.For(0, 100_000, _ => { if (gate.Request() != 0) Interlocked.Increment(ref scheduled); });
Check(scheduled == 0 && gate.Take(first), "one hundred thousand concurrent dirty events coalesce to the existing ticket");
Check(!gate.Take(first) && gate.SetActive(true) == 0, "idle output schedules no additional callback");
first = gate.Request(); gate.SetActive(false); long next = gate.SetActive(true);
Check(next > first && !gate.Take(first) && gate.Take(next), "stale callback cannot consume newer dirty state after hide/show");
gate.SetActive(false);
for (int i = 0; i < 100_000; i++) if (gate.Request() != 0) throw new Exception("Hidden callback scheduled");
next = gate.SetActive(true);
Check(next != 0 && gate.Take(next) && gate.SetActive(true) == 0, "hidden update bursts produce exactly one catch-up presentation");
for (int i = 0; i < 1000; i++)
{
    first = gate.Request();
    if (!gate.Take(first)) throw new Exception("Lost active dirty request");
}
Check(true, "repeated consume/request handoffs retain dirty state");
gate.Dispose(); Check(gate.Request() == 0 && gate.SetActive(true) == 0 && !gate.Take(next), "late callbacks after disposal are harmless");

var fit = DesktopViewportLayout.Calculate(1000, 1000, 2000, 1000, true);
Check(fit.Image == new Viewport(0, 250, 1000, 500) && !fit.HorizontalScroll && !fit.VerticalScroll, "fit letterboxes without changing server geometry");
Check(fit.Image.Map(500, 500, 2000, 1000) == ((ushort)1000, (ushort)500), "fit inverse input mapping");
var natural = DesktopViewportLayout.Calculate(800, 600, 1280, 720, false);
Check(natural.Width == 786 && natural.Height == 586 && natural.MaximumHorizontalOffset == 494 && natural.MaximumVerticalOffset == 134, "1:1 layout reserves both bars and exposes hidden pixels");
var far = DesktopViewportLayout.Calculate(800, 600, 1280, 720, false, 2, double.MaxValue, double.MaxValue);
Check(far.Image.Map(far.Width - .1, far.Height - .1, 1280, 720) == ((ushort)1279, (ushort)719), "zoomed pan reaches the exact remote bottom-right pixel");
var cross = DesktopViewportLayout.Calculate(800, 600, 800, 601, false);
Check(cross.HorizontalScroll && cross.VerticalScroll, "one scrollbar can induce overflow on the other axis");
var small = DesktopViewportLayout.Calculate(800, 600, 200, 200, false);
Check(small.Image == new Viewport(300, 200, 200, 200) && small.HorizontalOffset == 0, "unscaled smaller image centers with no scrolling");
Check(small.Image.Map(0, 0, 200, 200) is null, "letterbox does not forward pointer input");
Check(DesktopViewportLayout.Calculate(0, 600, 1280, 720, false) == default, "zero-sized unloaded viewport is empty");
foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
{
    Reject(() => DesktopViewportLayout.Calculate(invalid, 600, 1280, 720, true), "nonfinite layout rejected");
    Reject(() => DesktopViewportLayout.Calculate(800, 600, 1280, 720, false, horizontalOffset: invalid), "nonfinite offset rejected");
    Check(new Viewport(0, 0, 800, 600).Map(invalid, 1, 1280, 720) is null, "nonfinite pointer rejected");
    Check(Viewport.Fit(invalid, 600, 1280, 720) == default, "invalid fit area returns empty");
}
Reject(() => DesktopViewportLayout.Calculate(800, 600, 8192, 8192, false), "desktop allocation policy preserved");
Reject(() => DesktopViewportLayout.Calculate(800, 600, 1280, 720, false, zoom: 0), "zero zoom rejected");
Check(new Viewport(0,0,1,1).Map(.5,.5,0,1) is null, "invalid desktop dimensions cannot overflow input mapping");
var panned = new Viewport(-600, -300, 2560, 1440);
Check(panned.MapVisible(-1, 10, 1280, 720, 800, 600) is null, "uncaptured pointer outside viewport is rejected even if image extends past it");
Check(panned.MapVisible(-100, -100, 1280, 720, 800, 600, true) == ((ushort)300, (ushort)150), "captured drag clamps to visible top-left rather than off-screen image origin");
Check(panned.MapVisible(10000, 10000, 1280, 720, 800, 600, true) == ((ushort)699, (ushort)449), "captured drag clamps to visible bottom-right rather than desktop limit");
Check(small.Image.MapVisible(-100, -100, 200, 200, 800, 600, true) == ((ushort)0, (ushort)0), "captured letterbox drag clamps to image edge");
Check(panned.MapVisible(double.NaN, 0, 1280, 720, 800, 600, true) is null, "captured nonfinite pointer rejected before clamping");
Check(panned.MapVisible(1, 1, 1280, 720, 0, 600, true) is null, "unpainted viewport cannot forward input");
var random = new Random(423);
for (int i = 0; i < 200; i++)
{
    int width = random.Next(200, 3000), height = random.Next(200, 2000);
    double areaWidth = random.Next(160, 1600), areaHeight = random.Next(160, 1000), zoom = random.NextDouble() * 3.9 + .1;
    var layout = DesktopViewportLayout.Calculate(areaWidth, areaHeight, width, height, false, zoom, random.Next(4000), random.Next(4000));
    Check(layout.HorizontalOffset >= 0 && layout.HorizontalOffset <= layout.MaximumHorizontalOffset &&
        layout.VerticalOffset >= 0 && layout.VerticalOffset <= layout.MaximumVerticalOffset, "clamped random viewport " + i);
    var mapped = layout.Image.Map(layout.Width / 2, layout.Height / 2, width, height);
    if (mapped is not { } point || point.X >= width || point.Y >= height) throw new Exception("Center mapping escaped remote bounds");
}
for (int i = 0; i < 10; i++) _ = DesktopViewportLayout.Calculate(1000, 800, 1920, 1080, false);
long before = GC.GetAllocatedBytesForCurrentThread(); double sum = 0;
for (int i = 0; i < 100_000; i++) sum += DesktopViewportLayout.Calculate(1000, 800, 1920, 1080, false, 1.5, i, i).HorizontalOffset;
long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
Check(allocated == 0 && sum > 0, "100000 viewport layouts allocate zero managed bytes after warmup");
Console.WriteLine($"RESULT: {checks} presentation, viewport and invalid-input checks passed.");

AutomaticResizeTests.RunAsync().GetAwaiter().GetResult();

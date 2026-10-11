using RemoteSpace.Core;
using RemoteSpace.Input;
using RemoteSpace.Protocol;
using RemoteSpace.Transport;
using System.Diagnostics;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); checks++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name)
{
    bool failed = false;
    try { action(); } catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException) { failed = true; }
    Check(failed, name);
}
var caps = new InputCapabilities(true, true, true);
var events = new RemoteInput[] { new(InputKind.ScanCode,0,30), new(InputKind.ScanCode,0x8000,30),
    new(InputKind.Pointer,0x0800,0,18,22), new(InputKind.ExtendedPointer,0x8001,0,18,22),
    new(InputKind.ExtendedPointer,1,0,18,22), new(InputKind.Pointer,0x0478,0), new(InputKind.Synchronize,0,7) };
byte[] bytes = Activation.Inputs(0x103ea,1007,events);
var data = Activation.ParseData(Activation.ParseShares(bytes).Single().Body);
var reader = new PacketReader(data.Body);
Check(data.Type == 28 && reader.U16() == events.Length && reader.U16() == 0, "batch header contains actual event count");
ushort[] types = [4,4,0x8001,0x8002,0x8002,0x8001,0];
for (int i=0;i<events.Length;i++)
{
    Check(reader.U32() == 0 && reader.U16() == types[i], "ordered event type and timestamp " + i);
    if (events[i].Kind == InputKind.Synchronize) Check(reader.U16()==0 && reader.U32()==7, "synchronize flags");
    else { Check(reader.U16()==events[i].Flags, "event flags " + i); reader.Skip(4); }
}
reader.End();
Check(Activation.Input(1,1007,events[0]).SequenceEqual(Activation.Inputs(1,1007,events.AsSpan(0,1))), "single event and batch wire format match");
Reject(()=>Activation.Inputs(1,1007,[]), "empty batch rejected");
Reject(()=>Activation.Inputs(1,1007,new RemoteInput[65]), "oversized batch rejected");
foreach (var input in events) { RemoteInputValidation.Validate(input,caps); }
foreach (var input in new RemoteInput[] {new((InputKind)99,0,0),new(InputKind.ScanCode,1,30),new(InputKind.ScanCode,0,256),
    new(InputKind.Unicode,0x0100,65),new(InputKind.Synchronize,0,16),new(InputKind.Pointer,0x8000,0),
    new(InputKind.Pointer,0x0678,0),new(InputKind.ExtendedPointer,0x8004,0),new(InputKind.Pointer,0x0800,1)})
    Reject(()=>RemoteInputValidation.Validate(input),"reserved/contradictory input flags rejected: " + input.Kind);
Reject(()=>RemoteInputValidation.Validate(new(InputKind.Unicode,0,65),InputCapabilities.Basic),"unnegotiated Unicode rejected");
Reject(()=>RemoteInputValidation.Validate(new(InputKind.ExtendedPointer,1,0),InputCapabilities.Basic),"unnegotiated extended pointer rejected");
Reject(()=>RemoteInputValidation.Validate(new(InputKind.Pointer,0x0478,0),InputCapabilities.Basic),"unnegotiated horizontal wheel rejected");
Check(!RemoteInputValidation.IsPositioned(new(InputKind.Pointer,0x0278,0,65535,65535)),"wheel coordinates are not absolute pointer movement");
var state = new InputState();
Check(state.ExtendedButton(1,true,4,5) == new RemoteInput(InputKind.ExtendedPointer,0x8001,0,4,5),"fourth button down");
Check(state.ExtendedButton(1,true,4,5) is null,"duplicate fourth button down ignored");
state.ExtendedButton(2,true,4,5); state.Button(0x1000,true,4,5); state.Move(20,30);
var releases = state.ReleaseAll();
Check(releases.Length==3 && releases.Count(e=>e.Kind==InputKind.ExtendedPointer)==2 && releases.All(e=>e.X==20 && e.Y==30),"focus loss releases standard and extended buttons at current location");
Check(state.HeldButtonCount==0 && state.ReleaseAll().Length==0,"button releases are idempotent");
Check(state.Wheel(-120,0,0,true).Single().Flags==0x0588,"negative horizontal wheel signed-nine-bit encoding");
Check(InputState.PauseKey().Select(e=>e.Flags).SequenceEqual(new ushort[]{0x200,0,0x8200,0x8000}),"Pause key EXTENDED1 sequence");
Check(InputState.TryMapKey(0x2c,out var print) && print==(0x37,true),"PrintScreen physical key fallback");
foreach (ushort flags in new ushort[]{1,0x115,0xffff})
{
    var bitmap = new PacketWriter().U16(2).U16(16).U16(24).Zeros(6).U16(1280).U16(720).ToArray();
    var input = new PacketWriter().U16(13).U16(88).U16(flags).Zeros(82).ToArray();
    var sets = new PacketWriter().U16(2).U16(0).Bytes(bitmap).Bytes(input).ToArray();
    var demand = new PacketWriter().U32(1).U16(0).U16(sets.Length).Bytes(sets).U32(0).ToArray();
    var parsed = Activation.ParseDemand(demand);
    Check(parsed.InputCapabilities==new InputCapabilities((flags&16)!=0,(flags&4)!=0,(flags&256)!=0),"server input capability negotiation " + flags);
}
var control=BridgeWire.ParseControl(BridgeWire.Control(new(){Type="input-batch",Inputs=events}));
Check(control.Inputs!.SequenceEqual(events),"bridge batch JSON preserves event order and fields");
// Batching reduces both wire overhead and encoder allocations; no network/FPS claim.
RemoteInput[] sixtyFour = Enumerable.Range(0,64).Select(i=>new RemoteInput(InputKind.ScanCode,(ushort)((i&1)*0x8000),30)).ToArray();
_=Activation.Inputs(1,1007,sixtyFour);_=Activation.Input(1,1007,sixtyFour[0]);
long allocated=GC.GetAllocatedBytesForCurrentThread(); int singleBytes=0;
foreach(var e in sixtyFour) singleBytes+=Activation.Input(1,1007,e).Length;
long singleAllocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
allocated=GC.GetAllocatedBytesForCurrentThread(); int batchBytes=Activation.Inputs(1,1007,sixtyFour).Length;
long batchAllocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
Check(batchBytes==790 && singleBytes==2176,"64-event batch reduces share PDU bytes from 2176 to 790");
Check(batchAllocated<singleAllocated/2,"batched encoder allocation reduction");
Console.WriteLine($"PERFORMANCE input encoding: single={singleAllocated} allocated bytes; batch={batchAllocated}; input-only microbenchmark.");
QueueTests.Run(Check).GetAwaiter().GetResult();
Console.WriteLine($"RESULT: {checks} input checks passed.");

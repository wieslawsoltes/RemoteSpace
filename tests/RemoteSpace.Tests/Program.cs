using RemoteSpace.Protocol;
using RemoteSpace.Graphics;
using RemoteSpace.Profiles;

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); checks++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { bool thrown = false; try { action(); } catch (Exception e) when (e is ArgumentException or FormatException or IOException) { thrown = true; } Check(thrown, name); }
Check(Convert.ToHexString(Negotiation.Request(SecurityProtocols.Tls | SecurityProtocols.Hybrid)) == "030000130EE000000000000100080003000000", "X.224 negotiation golden bytes");
var confirm = Convert.FromHexString("0ED000000000000200080002000000");
Check(Negotiation.ParseConfirm(confirm, SecurityProtocols.Hybrid).Protocol == SecurityProtocols.Hybrid, "security negotiation");
Reject(() => Negotiation.ParseConfirm(confirm, SecurityProtocols.Tls), "security downgrade/unrequested selection rejected");
Reject(() => Negotiation.ParseConfirm(Convert.FromHexString("06D00000000000"), SecurityProtocols.Tls), "legacy security rejected");
for (int i = 0; i < confirm.Length; i++) { int n = i; Reject(() => Negotiation.ParseConfirm(confirm.AsSpan(0, n), SecurityProtocols.Hybrid), $"truncated negotiation {i}"); }
foreach (int n in new[] { 0, 1, 127, 128, 255, 1024, 32767 }) { var bytes = new PacketWriter().PerLength(n).ToArray(); Check(new PacketReader(bytes).PerLength() == n, $"PER length {n}"); }
foreach (uint n in new uint[] { 0, 127, 128, 255, 32768, uint.MaxValue }) { var encoded = new PacketWriter().BerInteger(n).ToArray(); var integer = new PacketReader(encoded).Ber(2); Check((integer[0] & 0x80) == 0, $"positive BER integer {n}"); }
Reject(() => new PacketReader(new byte[] { 4, 0x80 }).Ber(4), "indefinite BER forbidden");
Reject(() => new PacketReader(new byte[] { 0, 1 }).Bytes(-1), "negative slice rejected");
var wire = Framing.Data(new byte[] { 1, 2, 3, 4 });
var packet = await Framing.ReadAsync(new MemoryStream(wire));
Check(Framing.UnwrapData(packet.Payload).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "TPKT async framing round trip");
try { await Framing.ReadAsync(new MemoryStream(wire[..^1])); throw new Exception("Truncated stream accepted"); } catch (EndOfStreamException) { checks++; }
var fast = await Framing.ReadAsync(new MemoryStream(new byte[] { 0, 4, 1, 2 }));
Check(fast.IsFastPath && fast.Payload.SequenceEqual(new byte[] { 1, 2 }), "fast-path framing");
var surface = new FrameBuffer(); surface.Resize(2, 2);
surface.Apply(new SurfaceUpdate(1, 1, 1, 1, new byte[] { 8, 9, 10, 255 }));
Check(surface.Snapshot().Rgba[12] == 8, "dirty rectangle placement");
Reject(() => surface.Apply(new SurfaceUpdate(2, 0, 1, 1, new byte[4])), "surface bounds validation");
var shot = surface.Snapshot(); shot.Rgba[12] = 2; Check(surface.Snapshot().Rgba[12] == 8, "snapshot storage is isolated");
var viewport = Viewport.Fit(100, 100, 200, 100); Check(viewport.Y == 25, "letterbox transform");
Check(viewport.Map(50, 50, 200, 100) == ((ushort)100, (ushort)50), "inverse input transform");
Check(viewport.Map(1, 1, 200, 100) is null, "letterbox bars reject input");
var profile = new ConnectionProfile { Host = "::1", Port = 3390, UserName = "user:one", RedirectClipboard = true };
var imported = RdpFile.Parse(RdpFile.Export(profile)).Profile;
Check(imported.Host == "::1" && imported.Port == 3390 && imported.UserName == "user:one" && imported.RedirectClipboard, "RDP profile round trip with IPv6 and colon");
Check(RdpFile.Parse(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(RdpFile.Export(profile))).ToArray()).Profile.Host == "::1", "UTF16 LE RDP import");
Reject(() => RdpFile.Parse("full address:s:host\nfull address:s:other"), "duplicate endpoint rejection");
Reject(() => RdpFile.Parse("full address:i:12"), "typed field rejection");
Reject(() => RdpFile.Export(profile with { UserName = "a\nfull address:s:evil" }), "RDP line injection rejected");
Check(RdpFile.Parse("full address:s:host\npassword 51:b:secret\nredirectprinters:i:1").Warnings.Count == 2, "secret discard and unsupported option diagnostics");
Reject(() => new ConnectionOptions { Host = "host", Width = 8192, Height = 8192 }.Validate(), "desktop allocation bound");
Reject(() => new ConnectionOptions { Host = "host", CertificateSha256 = "00" }.Validate(), "pin length validation");
Check(!new SessionCredentials("user", "secret").ToString().Contains("secret"), "credentials redact ToString");
ProtocolTests.Run(Check, Reject);
DynamicChannelTests.Run(Check, Reject);
Check(Convert.ToHexString(Activation.Synchronize(0x000103ea, 1007, 1002)) == "16001700EF03EA030100000108001F0000000100EA03", "Microsoft client synchronize golden PDU");
Console.WriteLine($"RESULT: {checks} checks passed.");

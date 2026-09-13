using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

if (args.Length != 3) throw new ArgumentException("<fresh-output> <own-pid> <fresh-own-session>");
string output = Path.GetFullPath(args[0]); int pid = int.Parse(args[1]); string name = args[2];
if (Directory.Exists(output) || pid <= 0 || !name.StartsWith($"FWM_OWNED_CLR_{pid}_")) throw new ArgumentException("Output/session ownership.");
Directory.CreateDirectory(output);
void Write(string file, object value) { using var stream = new FileStream(Path.Combine(output, file), FileMode.CreateNew); JsonSerializer.Serialize(stream, value); }
if (TraceEventSession.GetActiveSessionNames().Contains(name)) throw new InvalidOperationException("Session name already exists.");
using var raw = new BinaryWriter(new FileStream(Path.Combine(output, "raw.bin"), FileMode.CreateNew));
using var json = new StreamWriter(new FileStream(Path.Combine(output, "events.jsonl"), FileMode.CreateNew));
using var session = new TraceEventSession(name, null, TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate);
session.StopOnDispose = false;
session.BufferSizeMB = 32;
long count = 0, rejected = 0; bool complete = false, created = false, enabled = false;
Exception? failure = null; Native.Stats? stopped = null; Task? consumer = null;
var runtime = session.Source.Clr;
var rundown = new ClrRundownTraceEventParser(session.Source);
long rundownComplete = 0;
void Record(TraceEvent data)
{
    if (data.ProcessID != pid) { Interlocked.Increment(ref rejected); return; }
    if (data.ProviderGuid != ClrTraceEventParser.ProviderGuid && data.ProviderGuid != ClrRundownTraceEventParser.ProviderGuid) throw new InvalidOperationException("Unexpected CLR provider.");
    byte[] payload = data.EventData();
    raw.Write(data.ProviderGuid.ToByteArray()); raw.Write(data.ProcessID); raw.Write(data.ThreadID); raw.Write(data.TimeStampQPC);
    raw.Write((ushort)data.ID); raw.Write((byte)data.Version); raw.Write((byte)data.Opcode); raw.Write(data.PointerSize); raw.Write(payload.Length); raw.Write(payload);
    var fields = new Dictionary<string, object?>();
    foreach (string field in data.PayloadNames) fields.Add(field, data.PayloadByName(field));
    json.WriteLine(JsonSerializer.Serialize(new { Sequence = ++count, Provider = data.ProviderGuid, Pid = data.ProcessID, Tid = data.ThreadID, Qpc = data.TimeStampQPC, Id = (int)data.ID, data.Version, Opcode = (int)data.Opcode, data.EventName, data.PointerSize, PayloadBytes = payload.Length, Fields = fields }));
    if (data.ProviderGuid == ClrRundownTraceEventParser.ProviderGuid && (int)data.ID == 146) Interlocked.Exchange(ref rundownComplete, data.TimeStampQPC);
}
runtime.All += Record; rundown.All += Record;
try
{
    var filter = new TraceEventProviderOptions { ProcessIDFilter = new List<int> { pid } };
    if (!TraceEventProviderOptions.FilteringSupported) throw new InvalidOperationException("PID filtering unsupported.");
    // Runtime Loader/JIT/NGen + EndEnumeration; rundown Loader/JIT/NGen +
    // EndRundown. Exact installed enum values and masks are retained below.
    ulong runtimeKeywords = 0xB8, rundownKeywords = 0x138;
    Write("session.json", new { Session = name, OwnPid = pid, CollectorPid = Environment.ProcessId, RuntimeProvider = ClrTraceEventParser.ProviderGuid, RundownProvider = ClrRundownTraceEventParser.ProviderGuid, RuntimeKeywords = runtimeKeywords, RundownKeywords = rundownKeywords, PidFilter = new[] { pid }, QpcFrequency = Stopwatch.Frequency, Qpc = Stopwatch.GetTimestamp(), NoRestartOnCreate = true, ConsumerDiscardsForeignBeforeStorage = true, RawSchema = "<16sIIqHBBII> + exact EventData bytes", RawHeaderFlagsClaim = false });
    created = true; enabled = session.EnableProvider(ClrTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, runtimeKeywords, filter);
    consumer = Task.Run(() => { complete = session.Source.Process(); });
    session.EnableProvider(ClrRundownTraceEventParser.ProviderGuid, TraceEventLevel.Verbose, rundownKeywords, filter);
    var watch = Stopwatch.StartNew();
    while (Volatile.Read(ref rundownComplete) == 0 && watch.Elapsed < TimeSpan.FromSeconds(30))
    { if (consumer.IsCompleted) { consumer.GetAwaiter().GetResult(); throw new InvalidOperationException("CLR consumer ended before rundown."); } Thread.Sleep(20); }
    if (Volatile.Read(ref rundownComplete) == 0) throw new InvalidOperationException("Own CLR rundown completion missing.");
    Console.WriteLine(JsonSerializer.Serialize(new { Ready = true, OwnPid = pid, CollectorPid = Environment.ProcessId, Session = name, RundownCompleteQpc = rundownComplete })); Console.Out.Flush();
    if (Console.ReadLine() != "stop") throw new InvalidOperationException("Owned control pipe closed before stop.");
}
catch (Exception e) { failure = e; }
finally
{
    if (created)
    {
        stopped = Native.Stop(name);
        try { if (consumer != null && !consumer.Wait(TimeSpan.FromSeconds(20))) { session.Source.StopProcessing(); failure ??= new TimeoutException("Own CLR consumer failed to drain."); consumer.Wait(TimeSpan.FromSeconds(5)); } }
        catch (Exception e) { failure ??= e; }
    }
    raw.Flush(); json.Flush();
    Write("summary.json", new { OwnPid = pid, CollectorPid = Environment.ProcessId, Session = name, Records = count, DiscardedForeignEvents = rejected, ProcessCompleted = complete, stopped, Failure = failure?.ToString(), RuntimeEnableReturnedRestartedSession = enabled, RundownCompleteQpc = rundownComplete, ProviderFilterClaim = rejected == 0, PerformanceClaim = false, WholeIdStatus = "IN_PROGRESS" });
}
if (failure != null || stopped == null || stopped.Code != 0 || stopped.EventsLost != 0 || stopped.LogBuffersLost != 0 || stopped.RealTimeBuffersLost != 0 || !complete || count == 0) throw new InvalidOperationException("CLR source incomplete.", failure);

internal static class Native
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint ControlTraceW(ulong handle, string name, nint properties, uint controlCode);
    internal sealed record Stats(uint Code, uint EventsLost, uint LogBuffersLost, uint RealTimeBuffersLost, uint BuffersWritten);
    internal static unsafe Stats Stop(string name)
    {
        const int bytes = 16384; nint memory = Marshal.AllocHGlobal(bytes);
        try
        {
            new Span<byte>((void*)memory, bytes).Clear(); Marshal.WriteInt32(memory, bytes); Marshal.WriteInt32(memory, 116, 120);
            uint result = ControlTraceW(0, name, memory, 1);
            return new(result, (uint)Marshal.ReadInt32(memory, 88), (uint)Marshal.ReadInt32(memory, 96), (uint)Marshal.ReadInt32(memory, 100), (uint)Marshal.ReadInt32(memory, 92));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}

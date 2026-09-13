using Microsoft.Diagnostics.Tracing;
using System.Text.Json;

if (args.Length != 3 || Directory.Exists(args[1])) throw new ArgumentException("<existing-etl> <fresh-output> <own-pid>");
int pid = int.Parse(args[2]);
Directory.CreateDirectory(args[1]);
using var output = new BinaryWriter(new FileStream(Path.Combine(args[1], "stacks.bin"), FileMode.CreateNew));
using var source = new ETWTraceEventSource(args[0]);
long count = 0, frames = 0;
source.Kernel.StackWalkStack += data =>
{
    if (data.ProcessID != pid || data.ThreadID <= 0 || data.FrameCount < 2) throw new InvalidOperationException("Foreign or invalid native stack.");
    output.Write(data.EventTimeStampQPC);
    output.Write(data.ProcessID);
    output.Write(data.ThreadID);
    output.Write(data.FrameCount);
    for (int i = 0; i < data.FrameCount; i++) output.Write(data.InstructionPointer(i));
    count++; frames += data.FrameCount;
};
bool complete = source.Process();
output.Flush();
using var summary = new FileStream(Path.Combine(args[1], "summary.json"), FileMode.CreateNew);
JsonSerializer.Serialize(summary, new { Completed = complete, OwnPid = pid, StackEvents = count, StackFrames = frames, source.EventsLost,
    ReaderAssembly = typeof(TraceEvent).Assembly.FullName, RecordedUtc = DateTime.UtcNow, OfflineOnly = true, TraceSessionCreated = false });
if (!complete || source.EventsLost != 0 || count == 0) throw new InvalidOperationException("Incomplete stack decode.");

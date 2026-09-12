namespace MyWhoosh2Garmin.Processes;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments);
}

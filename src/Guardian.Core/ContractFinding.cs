namespace Guardian.Core;

public enum FindingSeverity
{
    INFO = 1,
    WARN = 2,
    ERR = 3,
    UNKNOWN = 4,
}

public sealed record FindingLocation(int? Line, int? Column);

public sealed record ContractFinding(string RuleId, FindingSeverity Severity, string? Method, string? Path,
    string? Section, FindingLocation? BaseSource, FindingLocation? CandidateSource, string Message, string Fingerprint);

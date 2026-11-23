namespace Guardian.Core;

public enum GateExitCode
{
    Success = 0,
    PolicyFailure = 2,
    InvalidSpec = 3,
    EngineFailure = 4,
    InvalidPolicy = 5,
}

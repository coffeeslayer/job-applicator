public enum ScanCompletionAction
{
    Rescan,
    Exit,
    Invalid
}

public static class ScanCompletion
{
    public static ScanCompletionAction ParseAction(string? input)
    {
        if (input is null || input.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
        {
            return ScanCompletionAction.Exit;
        }

        return string.IsNullOrWhiteSpace(input)
            ? ScanCompletionAction.Rescan
            : ScanCompletionAction.Invalid;
    }
}

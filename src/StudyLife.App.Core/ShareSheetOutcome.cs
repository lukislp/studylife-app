namespace StudyLife.App.Core;

/// <summary>How the system share sheet for a download ended (iOS/Mac Catalyst
/// UIActivityViewController). Only <see cref="Failed"/> is an error: the user closing the sheet, or
/// cancelling "Save to Files" inside it, is a choice and must not show the client's error text.</summary>
public enum ShareSheetOutcome
{
    /// <summary>An activity finished: saved to Files, shared, AirDropped, ...</summary>
    Completed,
    /// <summary>The sheet or the chosen activity was closed without finishing.</summary>
    Cancelled,
    /// <summary>The chosen activity reported an error.</summary>
    Failed,
}

public static class ShareSheetResult
{
    /// <summary>Maps the sheet's completion callback (completed flag, error or none) to an outcome.
    /// A finished activity wins over an error it may still report; an unfinished one is a
    /// cancellation unless it came with an error.</summary>
    public static ShareSheetOutcome From(bool completed, bool hasError) =>
        completed ? ShareSheetOutcome.Completed
        : hasError ? ShareSheetOutcome.Failed
        : ShareSheetOutcome.Cancelled;
}

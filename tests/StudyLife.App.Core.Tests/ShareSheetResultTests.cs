using StudyLife.App.Core;

namespace StudyLife.App.Core.Tests;

public class ShareSheetResultTests
{
    [Theory]
    [InlineData(true, false, ShareSheetOutcome.Completed)]  // "Save to Files" saved, or shared
    [InlineData(true, true, ShareSheetOutcome.Completed)]   // finished, a late error does not undo it
    [InlineData(false, false, ShareSheetOutcome.Cancelled)] // sheet closed, or "Save to Files" cancelled
    [InlineData(false, true, ShareSheetOutcome.Failed)]     // the activity failed
    public void Maps_the_sheet_callback(bool completed, bool hasError, ShareSheetOutcome expected) =>
        Assert.Equal(expected, ShareSheetResult.From(completed, hasError));
}

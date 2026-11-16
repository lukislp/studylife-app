using Android.App;
using StudyLife.App.Core;
using StudyLife.App.Services;
using AlertDialog = AndroidX.AppCompat.App.AlertDialog;

namespace StudyLife.App;

/// <summary>
/// Any installed app can send ACTION_SEND straight to the main activity, without the user ever picking
/// StudyAtrium in a share sheet. So nothing becomes a note before the user confirmed it here: the dialog
/// shows the beginning of the shared text (SharedText.Preview) under the app's name; only OK queues it,
/// Cancel, Back or a tap outside drop it. Title and buttons are the app's name and the system's own
/// OK/Cancel, so the dialog is in the device language without texts of its own.
/// </summary>
internal static class SharedTextConfirmation
{
    public static void Ask(Activity activity, string? text, string? subject)
    {
        if (SharedText.Compose(text, subject) is not { } note) return;

        var builder = new AlertDialog.Builder(activity);
        builder.SetTitle(AppInfo.Current.Name);
        builder.SetMessage(SharedText.Preview(note));
        builder.SetCancelable(true);
        builder.SetNegativeButton(global::Android.Resource.String.Cancel, (_, _) => { });
        builder.SetPositiveButton(global::Android.Resource.String.Ok, (_, _) => SharedNoteIntake.SetPending(note));
        builder.Show();
    }
}

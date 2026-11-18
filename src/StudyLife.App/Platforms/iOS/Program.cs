using ObjCRuntime;
using UIKit;

namespace StudyLife.App;

public class Program
{
    // This is the main entry point of the application.
    static void Main(string[] args)
    {
        // StudyAtriumApplication sees hardware key events before any responder does (the iPad's
        // Left/Right arrow keys for the calendar, see its doc comment).
        UIApplication.Main(args, typeof(StudyAtriumApplication), typeof(AppDelegate));
    }
}

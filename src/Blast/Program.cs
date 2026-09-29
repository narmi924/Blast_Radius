using System.Text.Json;

if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("blast - local change review and conflict-aware recovery (stage 1 preview)");
    Console.WriteLine("Commands: doctor, run, report, undo");
    Console.WriteLine("The test runner creates isolated synthetic fixtures. User-directory run/apply is not enabled.");
    return 0;
}

if (args[0] == "doctor")
{
    var result = new
    {
        schema_version = 1,
        platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        user_directory_run = "disabled_pending_real_data_gate",
        user_directory_apply = "disabled_pending_real_data_gate",
        synthetic_test_command = "dotnet run --project tests/Blast.Tests -- all"
    };
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

if (args[0] is "run" or "undo" or "report" or "sessions" or "init")
{
    Console.Error.WriteLine("This command is not enabled for user directories. Stage 1 recovery is available only through automatically created test fixtures until the real-data gate passes.");
    return 4;
}

Console.Error.WriteLine("Unknown command. Run blast --help.");
return 2;

using System.Diagnostics.CodeAnalysis;
using StudyDesk.Server.Core.Configuration;

namespace StudyDesk.Server;

/// <summary>Starts the configured sync host or provisions one device through a local command.</summary>
public static class Program
{
    /// <summary>Runs the server without accepting credentials on the command line.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The process boundary must not print private configuration or database exceptions.")]
    public static async Task<int> Main(string[] args)
    {
        try
        {
            ServerSettings settings = ServerSettings.LoadFromEnvironment();
            if (args.Length == 0 || args is ["serve"])
            {
                await using WebApplication app = await ServerApplication.BuildAsync(settings).ConfigureAwait(false);
                await app.RunAsync().ConfigureAwait(false);
                return 0;
            }

            if (args is ["device", "create", "--name", var name, "--output", var output])
            {
                await PairingProvisioner.CreateOwnerAsync(settings, name, output).ConfigureAwait(false);
                Console.WriteLine("Device association created in the selected private file.");
                return 0;
            }

            Console.Error.WriteLine("Usage: serve | device create --name <label> --output <absolute-path.studydesk-pairing.json>");
            return 2;
        }
        catch (Exception)
        {
            // Configuration values and exceptions may contain private information and are not printed.
            Console.Error.WriteLine("The server could not start or provision the association. Check local configuration and private-file permissions.");
            return 1;
        }
    }
}

using SharpDeps.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
if (args is ["--vscode-mutation-helper", ..])
    return await CliMutationEntry.RunAsync(args[1..], Console.OpenStandardInput(), Console.Out,
        Console.Error, cancellation.Token);
return await CliApplication.RunAsync(args, Console.Out, Console.Error, cancellation.Token);

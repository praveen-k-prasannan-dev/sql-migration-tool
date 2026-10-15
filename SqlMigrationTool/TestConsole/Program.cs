using TestConsole;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var opts = Options.Parse(args);
if (opts is null)
{
    Options.PrintUsage();
    return 2;
}

try
{
    return opts.Command switch
    {
        "setup"     => await Commands.SetupAsync(opts),
        "roundtrip" => await Commands.RoundTripAsync(opts),
        "compare"   => await Commands.CompareAsync(opts),
        _           => 2
    };
}
catch (Exception ex)
{
    Out.Error($"FATAL: {ex.Message}");
    return 3;
}

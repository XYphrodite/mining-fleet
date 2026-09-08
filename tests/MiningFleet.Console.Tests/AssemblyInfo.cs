// AnsiConsole.Console is a global: markup tests swap it for a TestConsole, so nothing may
// run alongside them.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

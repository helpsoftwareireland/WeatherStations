using WeatherAggregation.Client;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return await ClientApplication.RunAsync(args, Console.Out, Console.Error, cancellationToken: cancellation.Token);

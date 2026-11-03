using Azure.Data.Tables;
using Llamachant.Count;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

// COUNT_STORAGE: the connection string of the storage account holding the pings table (set by count.bicep).
var connection = Environment.GetEnvironmentVariable("COUNT_STORAGE")
    ?? throw new InvalidOperationException("COUNT_STORAGE isn't set.");
builder.Services.AddSingleton(new CountStore(new TableClient(connection, CountStore.TableName)));
builder.Services.AddSingleton(TimeProvider.System);

builder.Build().Run();

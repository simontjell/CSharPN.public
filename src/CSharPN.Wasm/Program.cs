using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using CSharPN.Visualizer.Services;
using CSharPN.Wasm;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Same services as the Server host – they run client-side in WASM.
builder.Services.AddScoped<SimulationService>();
builder.Services.AddScoped<ModelCatalog>();

await builder.Build().RunAsync();

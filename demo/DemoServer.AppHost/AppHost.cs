var builder = DistributedApplication.CreateBuilder(args);

builder.AddDotnetProject("demoserver", "../DemoServer/DemoServer.csproj")
    .WithExternalHttpEndpoints();

builder.Build().Run();

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.DemoServer>("demoserver")
    .WithExternalHttpEndpoints();

builder.Build().Run();

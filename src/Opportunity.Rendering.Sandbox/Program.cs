using Opportunity.Rendering.Sandboxing;

// One document per process, started by the render worker (SandboxedRenderer); the protocol runs over stdin/stdout.
return RenderSandboxChild.Run(args, Console.OpenStandardInput(), Console.OpenStandardOutput());

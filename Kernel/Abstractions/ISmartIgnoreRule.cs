namespace DevProjex.Kernel.Abstractions;

public interface ISmartIgnoreRule
{
	SmartIgnoreResult Evaluate(string rootPath);
}

public interface IProjectRootFactsSmartIgnoreRule : ISmartIgnoreRule
{
	SmartIgnoreResult Evaluate(ProjectRootFacts rootFacts);
}

public interface ISmartIgnoreRuleDescriptorProvider
{
	SmartIgnoreRuleDescriptor Descriptor { get; }
}

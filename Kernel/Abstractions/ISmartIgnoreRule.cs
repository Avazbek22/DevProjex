namespace DevProjex.Kernel.Abstractions;

public interface ISmartIgnoreRule
{
	SmartIgnoreResult Evaluate(string rootPath);
}

public interface ISmartIgnoreRuleDescriptorProvider
{
	SmartIgnoreRuleDescriptor Descriptor { get; }
}

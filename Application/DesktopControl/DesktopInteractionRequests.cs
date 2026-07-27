namespace DevProjex.Application.DesktopControl;

public enum DesktopPreviewView
{
	Tree,
	Content,
	TreeContent
}

public sealed record DesktopOpenRequest(
	string? ProjectPath = null,
	bool UseLastProject = false,
	bool NewWindow = false,
	bool WaitForCompletion = false,
	bool OpenPreview = false,
	DesktopPreviewView PreviewView = DesktopPreviewView.TreeContent,
	TreeTextFormat? TreeFormat = null,
	string? Filter = null,
	string? Search = null,
	ProjectSelectionSpec? Selection = null,
	AppLanguage? Language = null,
	bool ElevationAttempted = false);

public abstract record DesktopInteractionRequest;

public sealed record DesktopStatusRequest : DesktopInteractionRequest;
public sealed record DesktopActivateRequest : DesktopInteractionRequest;
public sealed record DesktopOpenProjectRequest(DesktopOpenRequest Request) : DesktopInteractionRequest;
public sealed record DesktopPreviewRequest(bool IsOpen, DesktopPreviewView? View = null) : DesktopInteractionRequest;
public sealed record DesktopPreviewViewRequest(DesktopPreviewView View) : DesktopInteractionRequest;
public sealed record DesktopTreeFormatRequest(TreeTextFormat Format) : DesktopInteractionRequest;
public sealed record DesktopFilterRequest(string? Query) : DesktopInteractionRequest;

public enum DesktopSearchOperation
{
	Set,
	Next,
	Previous,
	Clear
}

public sealed record DesktopSearchRequest(
	DesktopSearchOperation Operation,
	string? Query = null) : DesktopInteractionRequest;

public sealed record DesktopInteractionResult(
	bool Success,
	string? ErrorCode = null,
	IReadOnlyDictionary<string, object?>? State = null);

public interface IDesktopInteractionHandler
{
	Task<DesktopInteractionResult> HandleAsync(
		DesktopInteractionRequest request,
		CancellationToken cancellationToken);
}

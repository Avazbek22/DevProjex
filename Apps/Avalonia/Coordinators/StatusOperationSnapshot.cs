namespace DevProjex.Avalonia.Coordinators;

public readonly record struct StatusOperationSnapshot(
    long OperationId,
    StatusOperationType OperationType,
    Action? CancelAction);

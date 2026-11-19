namespace DevProjex.Infrastructure.Secrets;

internal sealed class ResettableValue<T>(Func<T> factory) where T : class
{
	private readonly object sync = new();
	private T? value;

	public bool IsValueCreated => Volatile.Read(ref value) is not null;

	public T Value
	{
		get
		{
			if (Volatile.Read(ref value) is { } current) return current;
			lock (sync)
			{
				if (value is not null) return value;
				var created = factory();
				Volatile.Write(ref value, created);
				return created;
			}
		}
	}

	public void Reset()
	{
		lock (sync) Volatile.Write(ref value, null);
	}
}

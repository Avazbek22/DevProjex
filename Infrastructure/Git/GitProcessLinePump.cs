using System.Buffers;

namespace DevProjex.Infrastructure.Git;

internal static class GitProcessLinePump
{
	private const int ReadBufferCharacters = 4 * 1024;

	public static async Task ReadAsync(
		TextReader reader,
		int maximumFrameCharacters,
		Action<GitProcessLineFrame> onFrame,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(reader);
		ArgumentNullException.ThrowIfNull(onFrame);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrameCharacters);

		var buffer = ArrayPool<char>.Shared.Rent(ReadBufferCharacters);
		var frame = new BoundedFrameAssembler(maximumFrameCharacters);
		var skipLineFeed = false;
		try
		{
			while (true)
			{
				var read = await reader
					.ReadAsync(buffer.AsMemory(0, ReadBufferCharacters), cancellationToken)
					.ConfigureAwait(false);
				if (read == 0)
					break;

				foreach (var character in buffer.AsSpan(0, read))
				{
					if (skipLineFeed)
					{
						skipLineFeed = false;
						if (character == '\n')
							continue;
					}

					switch (character)
					{
						case '\r':
							onFrame(frame.Complete());
							skipLineFeed = true;
							break;
						case '\n':
							onFrame(frame.Complete());
							break;
						default:
							frame.Append(character);
							break;
					}
				}
			}

			if (frame.HasContent)
				onFrame(frame.Complete());
		}
		finally
		{
			ArrayPool<char>.Shared.Return(buffer, clearArray: true);
		}
	}

	private sealed class BoundedFrameAssembler(int maximumCharacters)
	{
		private readonly StringBuilder _content = new(Math.Min(ReadBufferCharacters, maximumCharacters));
		private bool _exceededLimit;

		public bool HasContent => _content.Length > 0 || _exceededLimit;

		public void Append(char character)
		{
			if (_content.Length < maximumCharacters)
				_content.Append(character);
			else
				_exceededLimit = true;
		}

		public GitProcessLineFrame Complete()
		{
			var result = new GitProcessLineFrame(_content.ToString(), _exceededLimit);
			_content.Clear();
			_exceededLimit = false;
			return result;
		}
	}
}

internal readonly record struct GitProcessLineFrame(string Text, bool ExceededLimit);

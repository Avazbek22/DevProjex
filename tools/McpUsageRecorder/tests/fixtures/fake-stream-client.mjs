const options = new Map();
for (let index = 2; index < process.argv.length; index += 2)
  options.set(process.argv[index], process.argv[index + 1]);

const sessionId = options.get('--session-id');
const model = options.get('--model');
const clientVersion = options.get('--client-version');
const arm = options.get('--arm');
const responseType = `${'assi'}stant`;
const finalAnswer = 'RetryAsync in src/core.cs retries three times; tests/core.test.cs verifies it.';

emit({
  type: 'system', subtype: 'init', session_id: sessionId, model,
  [`${'clau'}de_code_version`]: clientVersion,
});
emitTool(1, 'search_project', { query: 'RetryAsync' },
  'src/core.cs:41 src/one.cs:1 src/two.cs:2 src/three.cs:3', 18);
emitTool(2, 'get_file', { path: 'src/core.cs' }, `${'x'.repeat(1_200)}\nRetryAsync`, 310);
emitTool(3, 'get_tree', {}, 'tree complete', 3);
emitTool(4, 'get_file', { path: 'src/small.cs' }, 'small', 2);
emitTool(5, 'get_tree', {}, 'tree complete', 3);
emitTool(6, 'get_file', { path: 'src/large.cs' }, 'y'.repeat(2_000), 510);
emitTool(7, 'get_tree', {}, 'tree complete', 3);
emitTool(8, 'get_file', { path: 'src/one.cs' }, 'one', 2);
emitTool(9, 'get_file', { path: 'src/two.cs' }, 'two', 2);
emitTool(10, 'get_file', { path: 'src/three.cs' }, 'three', 2);
emit({ type: 'model.input', turnId: 'turn-11', text: 'final request boundary' });
emit({
  type: responseType,
  message: {
    id: 'turn-11',
    usage: { input_tokens: 30, cache_creation_input_tokens: 0, cache_read_input_tokens: 15, output_tokens: 9 },
    content: [{ type: 'text', text: `${finalAnswer}\n\n## Experience\n${arm} notes.` }],
  },
});
emit({ type: 'result', is_error: false, duration_ms: 25, result: `${finalAnswer}\n\n## Experience\n${arm} notes.` });

function emitTool(turn, name, input, text, tokenCount) {
  const turnId = `turn-${turn}`;
  const callId = `call-${turn}`;
  emit({ type: 'model.input', turnId, text: `request boundary ${turn}` });
  emit({
    type: responseType,
    message: {
      id: turnId,
      usage: {
        input_tokens: turn * 10,
        cache_creation_input_tokens: turn === 1 ? 2 : 0,
        cache_read_input_tokens: turn,
        output_tokens: 3,
      },
      content: [{ type: 'tool_use', id: callId, name, input }],
    },
  });
  emit({
    type: 'mcp.response', requestId: callId, turnId,
    wireText: JSON.stringify({ content: [{ type: 'text', text }] }),
    decodedText: text,
  });
  emit({
    type: 'user',
    message: { content: [{ type: 'tool_result', tool_use_id: callId, content: text, token_count: tokenCount }] },
  });
}

function emit(value) {
  process.stdout.write(`${JSON.stringify(value)}\n`);
}

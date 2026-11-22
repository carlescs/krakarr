import { describe, expect, it } from 'vitest';
import { readNdjson } from './ndjson';

function streamOf(...chunks: (string | Uint8Array)[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const c of chunks) controller.enqueue(typeof c === 'string' ? encoder.encode(c) : c);
      controller.close();
    },
  });
}

describe('readNdjson', () => {
  it('parses lines split across chunks, skipping blank ones and keeping a final line without a newline', async () => {
    const values: unknown[] = [];
    // "\u00e9" is two bytes in UTF-8: split it across chunks.
    const e = new TextEncoder().encode('\u00e9');
    await readNdjson(streamOf('{"a":1}\n{"b":"x', e.slice(0, 1), e.slice(1), '"}\n\n', '{"c":3}'), v => values.push(v));
    expect(values).toEqual([{ a: 1 }, { b: 'xé' }, { c: 3 }]);
  });
});

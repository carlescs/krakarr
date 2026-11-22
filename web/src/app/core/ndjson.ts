/** Reads newline-delimited JSON from a stream, handing out each value as soon as its line is complete. */
export async function readNdjson<T>(body: ReadableStream<Uint8Array>, onValue: (value: T) => void): Promise<void> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      buffer += decoder.decode(value, { stream: true }); // keeps multi-byte characters split across chunks intact
      let newline: number;
      while ((newline = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, newline).trim();
        buffer = buffer.slice(newline + 1);
        if (line) onValue(JSON.parse(line) as T);
      }
    }
    const rest = (buffer + decoder.decode()).trim();
    if (rest) onValue(JSON.parse(rest) as T);
  } finally {
    reader.releaseLock();
  }
}

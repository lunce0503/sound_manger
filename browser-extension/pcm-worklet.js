import {PcmBatcher} from './pcm-batcher.js';
class RawPcm extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.batcher = new PcmBatcher(options.processorOptions.channels, data => this.port.postMessage(data, [data]));
  }
  process(inputs, outputs) {
    this.batcher.push(inputs[0] ?? [], outputs[0]?.[0]?.length ?? 128);
    // Keep the audio graph running while playback goes to the server's selected output.
    for (const channel of outputs[0] ?? []) channel.fill(0);
    return true;
  }
}
registerProcessor('raw-pcm', RawPcm);

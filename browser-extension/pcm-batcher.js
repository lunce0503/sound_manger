export class PcmBatcher {
  constructor(channels, emit, frames = 1024) {
    this.channels = channels; this.frames = frames; this.emit = emit; this.position = 0;
    this.buffer = new ArrayBuffer(channels * frames * 4); this.view = new DataView(this.buffer);
  }
  push(input, count = input[0]?.length ?? 128) {
    for (let frame = 0; frame < count; frame++) {
      for (let channel = 0; channel < this.channels; channel++)
        this.view.setFloat32((this.position * this.channels + channel) * 4, input[channel]?.[frame] ?? input[0]?.[frame] ?? 0, true);
      if (++this.position === this.frames) {
        this.emit(this.buffer); this.position = 0;
        this.buffer = new ArrayBuffer(this.channels * this.frames * 4); this.view = new DataView(this.buffer);
      }
    }
  }
}

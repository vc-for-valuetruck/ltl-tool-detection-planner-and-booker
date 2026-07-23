/**
 * Voice narration for the Demo Director (branch: feat/demo-director-voice).
 *
 * A thin, defensive wrapper over the browser's Web Speech API (`window.speechSynthesis`).
 * It exists so the {@link DemoDirectorService} can *speak* each step's caption without owning
 * any of the platform quirks: missing API, no installed voices, engines that never fire
 * `onend`, SSR/test contexts with no `window`, etc.
 *
 * Guarantees:
 *  - Fully optional: when the API is unavailable every method is a safe no-op and
 *    {@link available} reports false so the UI can hide the toggle.
 *  - Never throws: the synthesis engine is flaky across browsers; all calls are guarded.
 *  - Honest speaking state: {@link speaking} flips false on end/error so a caller can gate
 *    auto-advance on "is the sentence still being spoken?" (with its own time cap — a hung
 *    engine must never stall the walkthrough).
 */
export class DemoDirectorNarrator {
  private readonly synth: SpeechSynthesis | null =
    typeof window !== 'undefined' && 'speechSynthesis' in window ? window.speechSynthesis : null;

  private active: SpeechSynthesisUtterance | null = null;
  private isSpeakingFlag = false;

  /** True when the platform can synthesise speech at all. */
  get available(): boolean {
    return this.synth !== null && typeof SpeechSynthesisUtterance !== 'undefined';
  }

  /** True while an utterance we started is still being spoken. */
  get speaking(): boolean {
    return this.isSpeakingFlag;
  }

  /**
   * Speaks `text` with sensible en-US defaults (rate ~1.0). Cancels anything currently
   * being spoken first so captions never overlap. No-op when unavailable or text is blank.
   */
  speak(text: string): void {
    if (!this.available) return;
    const trimmed = text?.trim();
    if (!trimmed) return;
    this.cancel();
    try {
      const utterance = new SpeechSynthesisUtterance(trimmed);
      utterance.lang = 'en-US';
      utterance.rate = 1.0;
      utterance.pitch = 1.0;
      utterance.volume = 1.0;
      const voice = this.pickVoice();
      if (voice) utterance.voice = voice;
      utterance.onend = () => {
        if (this.active === utterance) this.isSpeakingFlag = false;
      };
      utterance.onerror = () => {
        if (this.active === utterance) this.isSpeakingFlag = false;
      };
      this.active = utterance;
      this.isSpeakingFlag = true;
      this.synth!.speak(utterance);
    } catch {
      // A throwing synthesis engine must not break playback.
      this.active = null;
      this.isSpeakingFlag = false;
    }
  }

  /** Immediately stops any in-flight narration. Safe to call repeatedly / when unavailable. */
  cancel(): void {
    this.active = null;
    this.isSpeakingFlag = false;
    if (!this.available) return;
    try {
      this.synth!.cancel();
    } catch {
      // ignore — cancelling an idle engine can throw on some browsers.
    }
  }

  /** Prefer an en-US voice, then any English voice, else let the engine choose (null). */
  private pickVoice(): SpeechSynthesisVoice | null {
    try {
      const voices = this.synth?.getVoices?.() ?? [];
      return (
        voices.find((v) => /en[-_]US/i.test(v.lang)) ??
        voices.find((v) => /^en/i.test(v.lang)) ??
        null
      );
    } catch {
      return null;
    }
  }
}

/** localStorage key for the persisted narration on/off preference. */
export const DIRECTOR_NARRATION_KEY = 'ltl.demo.director.narration';

/**
 * Hard cap (ms) on how long auto-advance will wait for a spoken caption to finish before
 * proceeding anyway. Protects the run from a synthesis engine that never fires `onend`.
 */
export const DIRECTOR_SPEECH_CAP_MS = 15_000;

/**
 * Voice narration for the Demo Director.
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

/**
 * The three narrator personas the operator can pick from the control bar:
 *  - `auFemale` (default): a warm, natural Australian female voice — reads as roughly 29-35. This
 *     is the CFO-facing house voice, at a natural ~1.0 rate.
 *  - `narrator`: the deepest, most measured male en-US voice available, at a calm documentary rate.
 *  - `system`: whatever the platform picks by itself (no voice override).
 */
export type DirectorVoicePreset = 'narrator' | 'auFemale' | 'system';

export interface DirectorVoiceOption {
  readonly id: DirectorVoicePreset;
  readonly label: string;
}

/** Ordered voice options for the control-bar picker (first is the default). */
export const DIRECTOR_VOICE_OPTIONS: readonly DirectorVoiceOption[] = [
  { id: 'auFemale', label: 'Australian female' },
  { id: 'narrator', label: 'Narrator (deep male)' },
  { id: 'system', label: 'System default' },
];

export const DIRECTOR_DEFAULT_VOICE: DirectorVoicePreset = 'auFemale';

export class DemoDirectorNarrator {
  private readonly synth: SpeechSynthesis | null =
    typeof window !== 'undefined' && 'speechSynthesis' in window ? window.speechSynthesis : null;

  private active: SpeechSynthesisUtterance | null = null;
  private isSpeakingFlag = false;
  /** Remaining sentences to speak after {@link active} finishes (caption → posture, etc.). */
  private queue: string[] = [];

  /** Which persona to synthesise with. Mutated by {@link setPreset} from the control bar. */
  private preset: DirectorVoicePreset = DIRECTOR_DEFAULT_VOICE;

  /** True when the platform can synthesise speech at all. */
  get available(): boolean {
    return this.synth !== null && typeof SpeechSynthesisUtterance !== 'undefined';
  }

  /** True while an utterance we started is still being spoken. */
  get speaking(): boolean {
    return this.isSpeakingFlag;
  }

  /** Switches the narrator persona for subsequent utterances. */
  setPreset(preset: DirectorVoicePreset): void {
    this.preset = preset;
  }

  /**
   * Speaks `text` in the selected persona. A single string is spoken as one utterance; an array is
   * spoken sequentially — each part starts only when the previous one ends — so a step's caption and
   * its honest-state posture are both narrated, in order, with no overlap and nothing left silent.
   * {@link speaking} stays true until the whole queue drains. Cancels anything in flight first.
   * No-op when unavailable or every part is blank.
   */
  speak(text: string | readonly string[]): void {
    if (!this.available) return;
    const parts = (Array.isArray(text) ? [...text] : [text])
      .map((t) => (t ?? '').trim())
      .filter((t) => t.length > 0);
    if (parts.length === 0) return;
    this.cancel();
    this.queue = parts.slice(1);
    this.speakOne(parts[0]);
  }

  /** Speaks a single sentence; chains to the next queued sentence (or clears the flag) on end. */
  private speakOne(text: string): void {
    try {
      const utterance = new SpeechSynthesisUtterance(text);
      const voice = this.pickVoice();
      if (voice) utterance.voice = voice;
      // A deep-male narrator reads a touch slower and lower for gravitas; the other personas keep a
      // natural cadence. Default the language to the chosen voice's, else en-US.
      const isNarrator = this.preset === 'narrator';
      utterance.lang = voice?.lang || (this.preset === 'auFemale' ? 'en-AU' : 'en-US');
      utterance.rate = isNarrator ? 0.92 : 1.0;
      utterance.pitch = isNarrator ? 0.92 : 1.0;
      utterance.volume = 1.0;
      utterance.onend = () => this.onUtteranceDone(utterance);
      utterance.onerror = () => this.onUtteranceDone(utterance);
      this.active = utterance;
      this.isSpeakingFlag = true;
      this.synth!.speak(utterance);
    } catch {
      // A throwing synthesis engine must not break playback.
      this.active = null;
      this.isSpeakingFlag = false;
      this.queue = [];
    }
  }

  /** Advances the queue: speak the next sentence, or drop the speaking flag when done. */
  private onUtteranceDone(finished: SpeechSynthesisUtterance): void {
    // Ignore a late end from a superseded utterance (a newer speak()/cancel() has moved on).
    if (this.active !== finished) return;
    const next = this.queue.shift();
    if (next !== undefined) {
      this.speakOne(next);
    } else {
      this.active = null;
      this.isSpeakingFlag = false;
    }
  }

  /** Immediately stops any in-flight narration and clears the queue. Safe to call repeatedly. */
  cancel(): void {
    this.active = null;
    this.isSpeakingFlag = false;
    this.queue = [];
    if (!this.available) return;
    try {
      this.synth!.cancel();
    } catch {
      // ignore — cancelling an idle engine can throw on some browsers.
    }
  }

  /**
   * Picks the installed voice for the current {@link preset}:
   *  - `narrator`: score every en-US voice by how "deep male narrator" its name reads (Guy / Davis /
   *     Christopher / Eric / Aaron / Brandon / Roger / Steffan / Microsoft Guy Online …), giving a
   *     strong bonus to 'Natural' / 'Online' neural voices, then any male-sounding en-US voice, then
   *     any en-US, then any English. Names vary by platform, so a miss simply falls through a tier.
   *  - `auFemale`: an Australian female voice, then any en-AU, then a GB female, then any English.
   *  - `system`: no override (let the engine choose).
   */
  private pickVoice(): SpeechSynthesisVoice | null {
    try {
      const voices = this.synth?.getVoices?.() ?? [];
      if (voices.length === 0 || this.preset === 'system') return null;
      return this.preset === 'auFemale' ? this.pickAuFemale(voices) : this.pickNarrator(voices);
    } catch {
      return null;
    }
  }

  private pickNarrator(voices: SpeechSynthesisVoice[]): SpeechSynthesisVoice | null {
    const enUs = voices.filter((v) => /en[-_]US/i.test(v.lang));
    const pool = enUs.length > 0 ? enUs : voices.filter((v) => /^en/i.test(v.lang));
    if (pool.length === 0) return null;
    const scored = pool
      .map((v) => ({ v, score: this.narratorScore(v) }))
      .sort((a, b) => b.score - a.score);
    // Prefer the highest-scoring candidate; if nothing scored positively, fall back to the first
    // en-US voice rather than leaving the engine to pick an arbitrary (possibly female) default.
    return scored[0]?.score > 0 ? scored[0].v : (enUs[0] ?? pool[0] ?? null);
  }

  private narratorScore(v: SpeechSynthesisVoice): number {
    const name = v.name;
    let score = 0;
    if (DemoDirectorNarrator.DEEP_MALE_VOICE_NAME.test(name)) score += 10;
    else if (DemoDirectorNarrator.MALE_VOICE_NAME.test(name)) score += 5;
    // Neural voices sound far more natural — strongly preferred for a boardroom demo.
    if (/natural/i.test(name)) score += 4;
    if (/online/i.test(name)) score += 2;
    // Never let an obviously-female name win the male-narrator slot.
    if (DemoDirectorNarrator.FEMALE_VOICE_NAME.test(name)) score -= 12;
    return score;
  }

  private pickAuFemale(voices: SpeechSynthesisVoice[]): SpeechSynthesisVoice | null {
    const isAU = (v: SpeechSynthesisVoice) => /en[-_]AU/i.test(v.lang);
    const isGB = (v: SpeechSynthesisVoice) => /en[-_]GB/i.test(v.lang);
    const isFemale = (v: SpeechSynthesisVoice) => DemoDirectorNarrator.FEMALE_VOICE_NAME.test(v.name);
    return (
      voices.find((v) => isAU(v) && isFemale(v)) ??
      voices.find((v) => isAU(v)) ??
      voices.find((v) => isGB(v) && isFemale(v)) ??
      voices.find((v) => /en[-_]US/i.test(v.lang) && isFemale(v)) ??
      voices.find((v) => /^en/i.test(v.lang)) ??
      null
    );
  }

  /** Leading deep-male en-US narrator voice names across Windows/Edge, macOS and Chrome. */
  private static readonly DEEP_MALE_VOICE_NAME =
    /\bguy\b|davis|christopher|eric|aaron|brandon|roger|steffan|\bbrian\b|\bdaniel\b|\bfred\b|\brishi\b/i;

  /** Broader male-name heuristic used as a second tier. */
  private static readonly MALE_VOICE_NAME =
    /\bmale\b|\bmark\b|\bmatthew\b|\bmichael\b|\bjames\b|\bpaul\b|\btom\b|\bthomas\b|\bgeorge\b|\balex\b|\barthur\b|\bjustin\b|\blee\b|\bnathan\b|\bsteve\b|\bwilliam\b/i;

  /** Best-effort female-voice name heuristic — leads with the common en-AU female voices. */
  private static readonly FEMALE_VOICE_NAME =
    /karen|catherine|hayley|kylie|female|samantha|tessa|fiona|serena|moira|veena|zira|susan|linda|heather|nicky|kate|olivia|joanna|amy|emma|libby|aria|jenny|michelle|clara|natasha/i;
}

/** localStorage key for the persisted narration on/off preference. */
export const DIRECTOR_NARRATION_KEY = 'ltl.demo.director.narration';

/**
 * localStorage key for the persisted voice-persona preference. Bumped to `.v2` alongside the change
 * of the default persona to the Australian female house voice: only ever written on an explicit pick,
 * so an existing operator who chose a persona under the old key is migrated to the new default and
 * keeps it unless they deliberately pick again after this change (which persists under the new key).
 */
export const DIRECTOR_VOICE_KEY = 'ltl.demo.director.voice.v2';

/**
 * Hard cap (ms) on how long auto-advance will wait for a spoken caption to finish before proceeding
 * anyway. Generous so the unhurried documentary captions are always spoken in full — it is purely a
 * safety net against a synthesis engine that never fires `onend`, never a routine truncation.
 */
export const DIRECTOR_SPEECH_CAP_MS = 45_000;

import { DIRECTOR_NARRATION_KEY, DIRECTOR_SPEECH_CAP_MS, DemoDirectorNarrator } from './demo-director.speech';

describe('DemoDirectorNarrator', () => {
  it('exposes a 15s speech cap constant and a stable storage key', () => {
    expect(DIRECTOR_SPEECH_CAP_MS).toBe(15_000);
    expect(DIRECTOR_NARRATION_KEY).toBe('ltl.demo.director.narration');
  });

  it('reports availability from the platform and never throws on speak/cancel', () => {
    const narrator = new DemoDirectorNarrator();
    // Whatever the platform reports, the API must be a safe no-op — not throw.
    expect(() => narrator.speak('hello world')).not.toThrow();
    expect(() => narrator.speak('')).not.toThrow();
    expect(() => narrator.cancel()).not.toThrow();
    expect(typeof narrator.available).toBe('boolean');
    expect(typeof narrator.speaking).toBe('boolean');
  });

  // Exercises the private voice-selection logic directly (via the fake synth's getVoices) so the
  // assertion does not depend on a real browser accepting a synthetic SpeechSynthesisVoice object.
  const pickWith = (voices: Array<{ name: string; lang: string }>) => {
    const narrator = new DemoDirectorNarrator();
    (narrator as unknown as { synth: unknown }).synth = { getVoices: () => voices };
    return (
      narrator as unknown as { pickVoice(): SpeechSynthesisVoice | null }
    ).pickVoice();
  };

  it('prefers an Australian female voice over an en-AU male and an en-GB female', () => {
    const picked = pickWith([
      { name: 'Daniel', lang: 'en-GB' },
      { name: 'Catherine', lang: 'en-GB' },
      { name: 'Lee', lang: 'en-AU' },
      { name: 'Karen', lang: 'en-AU' },
      { name: 'Samantha', lang: 'en-US' },
    ]);
    expect(picked?.name).toBe('Karen');
    expect(picked?.lang).toBe('en-AU');
  });

  it('falls back to any en-AU voice, then en-GB female, then en', () => {
    expect(pickWith([{ name: 'Rishi', lang: 'en-GB' }, { name: 'Lee', lang: 'en-AU' }])?.name).toBe('Lee');
    expect(
      pickWith([{ name: 'Mark', lang: 'en-US' }, { name: 'Catherine', lang: 'en-GB' }])?.name,
    ).toBe('Catherine');
    expect(pickWith([{ name: 'Mark', lang: 'en-US' }])?.name).toBe('Mark');
    expect(pickWith([{ name: 'SomeVoice', lang: 'fr-FR' }])).toBeNull();
  });

  it('is a no-op (not speaking) when the platform has no speech synthesis', () => {
    // Simulate an unavailable engine by pointing the wrapper at a context without the API.
    const narrator = new DemoDirectorNarrator();
    // Force the private synth reference to null to exercise the unavailable path deterministically.
    (narrator as unknown as { synth: unknown }).synth = null;
    expect(narrator.available).toBeFalse();
    narrator.speak('nothing should happen');
    expect(narrator.speaking).toBeFalse();
    expect(() => narrator.cancel()).not.toThrow();
  });
});

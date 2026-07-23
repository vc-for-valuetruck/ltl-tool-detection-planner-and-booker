import {
  DIRECTOR_NARRATION_KEY,
  DIRECTOR_SPEECH_CAP_MS,
  DemoDirectorNarrator,
  DirectorVoicePreset,
} from './demo-director.speech';

describe('DemoDirectorNarrator', () => {
  it('exposes a generous speech cap constant and a stable storage key', () => {
    expect(DIRECTOR_SPEECH_CAP_MS).toBe(45_000);
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
  const pickWith = (
    voices: Array<{ name: string; lang: string }>,
    preset: DirectorVoicePreset = 'auFemale',
  ) => {
    const narrator = new DemoDirectorNarrator();
    narrator.setPreset(preset);
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

  it('narrator preset prefers a deep-male natural en-US voice over a female en-US voice', () => {
    const picked = pickWith(
      [
        { name: 'Microsoft Aria Online (Natural) - English (United States)', lang: 'en-US' },
        { name: 'Microsoft Guy Online (Natural) - English (United States)', lang: 'en-US' },
        { name: 'Samantha', lang: 'en-US' },
      ],
      'narrator',
    );
    expect(picked?.name).toContain('Guy');
    expect(picked?.lang).toBe('en-US');
  });

  it('system preset applies no voice override', () => {
    expect(pickWith([{ name: 'Guy', lang: 'en-US' }], 'system')).toBeNull();
  });

  it('speaks an array of parts sequentially — caption, then posture — with no gap left silent', () => {
    const narrator = new DemoDirectorNarrator();
    if (!narrator.available) {
      pending('speechSynthesis unavailable in this environment');
      return;
    }
    const spoken: string[] = [];
    let lastUtterance: { onend?: () => void } | null = null;
    (narrator as unknown as { synth: unknown }).synth = {
      getVoices: () => [],
      cancel: () => {},
      speak: (u: { text: string; onend?: () => void }) => {
        spoken.push(u.text);
        lastUtterance = u;
      },
    };

    narrator.speak(['caption sentence', 'posture disclaimer']);
    // Only the first part starts immediately; speaking stays true until the queue drains.
    expect(spoken).toEqual(['caption sentence']);
    expect(narrator.speaking).toBeTrue();

    // Finishing the caption chains straight into the posture — nothing on screen is left unspoken.
    lastUtterance!.onend!();
    expect(spoken).toEqual(['caption sentence', 'posture disclaimer']);
    expect(narrator.speaking).toBeTrue();

    // Finishing the last part drops the speaking flag so auto-advance can proceed.
    lastUtterance!.onend!();
    expect(narrator.speaking).toBeFalse();
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

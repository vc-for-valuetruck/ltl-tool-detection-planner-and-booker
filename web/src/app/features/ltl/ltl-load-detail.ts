import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { LtlService } from './ltl.service';
import { LtlLoadSummary, LtlPlace, MatchFactor, MatchResult } from './ltl.models';
import { LtlDocumentUpload } from './ltl-document-upload';
import { YardArtifactFileView, YardArtifactView } from './yard-artifacts.models';

/** The familiar TMS load-detail tab layout. */
type DetailTab = 'details' | 'documents' | 'tracking' | 'billing';

/**
 * Internal, read-only load-detail view (issue #104). Replaces the old `va336.alvys.com/loads/{n}`
 * deep-link, which 404'd because Alvys has no per-tenant subdomain and no stable public deep-link
 * by load number. Backed by `GET /api/ltl/loads/{loadNumber}` (the normalized LtlLoadSummary).
 * A secondary "Open Alvys" link points at the Alvys home (app.alvys.com) — honest about the fact
 * that the dispatcher must search the load number there, rather than pretending a deep link exists.
 * Nothing writes back to Alvys.
 */
@Component({
  selector: 'app-ltl-load-detail',
  standalone: true,
  imports: [DatePipe, RouterLink, LtlDocumentUpload],
  templateUrl: './ltl-load-detail.html',
  styleUrls: ['./ltl-worklist.css', './ltl-load-detail.css'],
})
export class LtlLoadDetail implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly ltl = inject(LtlService);

  protected readonly loadNumber = signal<string>('');
  protected readonly load = signal<LtlLoadSummary | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  /** Yard artifacts (Phase 8.2) attached to this load number. Internal dock data, never Alvys. */
  protected readonly artifacts = signal<YardArtifactView[]>([]);

  /** Ranked, explainable driver/truck/trailer matches (Phase 2). Read-only against Alvys. */
  protected readonly matches = signal<MatchResult[]>([]);
  protected readonly matchesLoading = signal(false);
  protected readonly matchesError = signal<string | null>(null);
  protected readonly copiedMatchKey = signal<string | null>(null);

  /** Alvys home — NOT a per-load deep link (none exists publicly). Dispatcher searches by number. */
  protected readonly alvysHomeUrl = 'https://app.alvys.com/';

  protected readonly hasLoad = computed(() => this.load() !== null);

  /** Active load-detail tab (Details / Documents / Tracking / Billing). */
  protected readonly tab = signal<DetailTab>('details');
  protected readonly tabs: readonly { readonly id: DetailTab; readonly label: string }[] = [
    { id: 'details', label: 'Details' },
    { id: 'documents', label: 'Documents' },
    { id: 'tracking', label: 'Tracking' },
    { id: 'billing', label: 'Billing' },
  ];

  /** Count of read-only document artifacts (#141 yard surfaces) — powers the Documents tab badge. */
  protected readonly documentCount = computed(() => this.artifacts().length);

  protected setTab(tab: DetailTab): void {
    this.tab.set(tab);
  }

  ngOnInit(): void {
    this.loadNumber.set(this.route.snapshot.paramMap.get('loadNumber') ?? '');
    this.fetch();
  }

  protected fetch(): void {
    const ref = this.loadNumber();
    if (!ref) {
      this.error.set('No load number in the URL.');
      this.loading.set(false);
      return;
    }
    this.loading.set(true);
    this.error.set(null);
    this.ltl.getLoad(ref).subscribe({
      next: (load) => {
        this.load.set(load);
        this.loading.set(false);
        this.fetchArtifacts(load.loadNumber ?? ref);
        this.fetchMatches(load.loadNumber ?? load.id ?? ref);
      },
      error: (err) => {
        this.error.set(err?.error?.error ?? err?.message ?? "Couldn't reach Alvys.");
        this.loading.set(false);
      },
    });
  }

  private fetchArtifacts(loadNumber: string): void {
    if (!loadNumber) return;
    this.ltl.yardArtifacts({ loadNumber }).subscribe({
      next: (artifacts) => this.artifacts.set(artifacts),
      // Yard artifacts are supplementary; a failure here must never blank out the load detail.
      error: () => this.artifacts.set([]),
    });
  }

  private fetchMatches(idOrNumber: string): void {
    if (!idOrNumber) return;
    this.matchesLoading.set(true);
    this.matchesError.set(null);
    this.ltl.getMatches(idOrNumber).subscribe({
      next: (matches) => {
        this.matches.set(matches);
        this.matchesLoading.set(false);
      },
      // Matches are decision support, not the primary detail; a failure here shows an inline
      // error in the matches card rather than blanking the whole load view.
      error: (err) => {
        this.matchesError.set(err?.error?.error ?? err?.message ?? "Couldn't reach Alvys.");
        this.matchesLoading.set(false);
      },
    });
  }

  protected matchKey(match: MatchResult, index: number): string {
    return `${match.driverId ?? ''}|${match.truckId ?? ''}|${match.trailerId ?? ''}|${index}`;
  }

  protected matchLabelClass(label: string): string {
    if (label === 'Excellent' || label === 'Good') return 'chip chip-good';
    if (label === 'Possible') return 'chip chip-neutral';
    if (label === 'NotRecommended') return 'chip chip-danger';
    return 'chip chip-warn';
  }

  protected factorRowClass(status: string): string {
    if (status === 'Strong') return 'factor-row factor-strong';
    if (status === 'Weak') return 'factor-row factor-weak';
    if (status === 'Unavailable') return 'factor-row factor-unavailable';
    return 'factor-row factor-neutral';
  }

  /** "12 / 15 pts" for a scored factor; "not scored" for an Unavailable one (MaxPoints 0). */
  protected factorContribution(factor: MatchFactor): string {
    if (factor.status === 'Unavailable' || factor.maxPoints <= 0) return 'not scored';
    const earned = Math.round(factor.points * 10) / 10;
    return `${earned} / ${factor.maxPoints} pts`;
  }

  /** Human-readable breakdown for the copy-to-clipboard button — audit-friendly, plain text. */
  protected matchClipboardText(match: MatchResult): string {
    const lines: string[] = [];
    const who = match.driverName ?? match.driverId ?? 'Unnamed driver';
    lines.push(`${match.labelText} (${match.score}/100) — ${who}`);
    if (match.truckNumber) lines.push(`Truck ${match.truckNumber}`);
    if (match.trailerNumber) lines.push(`Trailer ${match.trailerNumber}`);
    lines.push(match.summary);
    lines.push('');
    lines.push('Factors:');
    for (const f of match.factors) {
      const contribution = this.factorContribution(f);
      const raw = f.rawValue ? ` [${f.rawValue}]` : '';
      lines.push(`- ${f.name} (${f.status}, ${contribution})${raw}: ${f.detail}`);
    }
    if (match.disqualifiers.length) {
      lines.push('');
      lines.push('Disqualifiers:');
      for (const d of match.disqualifiers) lines.push(`- ${d}`);
    }
    if (match.warnings?.length) {
      lines.push('');
      lines.push('Warnings:');
      for (const w of match.warnings) lines.push(`- ${w}`);
    }
    if (match.predictionBasis) {
      lines.push('');
      lines.push(`Ranking basis: ${match.predictionBasis}`);
    }
    return lines.join('\n');
  }

  protected copyMatch(match: MatchResult, index: number): void {
    const key = this.matchKey(match, index);
    const text = this.matchClipboardText(match);
    const done = () => {
      this.copiedMatchKey.set(key);
      setTimeout(() => {
        if (this.copiedMatchKey() === key) this.copiedMatchKey.set(null);
      }, 2000);
    };
    if (navigator?.clipboard?.writeText) {
      navigator.clipboard.writeText(text).then(done, done);
    } else {
      done();
    }
  }

  protected fileUrl(artifactId: string, file: YardArtifactFileView): string {
    return this.ltl.yardArtifactFileUrl(artifactId, file.id);
  }

  protected photos(artifact: YardArtifactView): YardArtifactFileView[] {
    return artifact.files.filter((f) => f.kind === 'Photo');
  }

  protected pdfs(artifact: YardArtifactView): YardArtifactFileView[] {
    return artifact.files.filter((f) => f.kind === 'Pdf');
  }

  protected artifactChipClass(status: string): string {
    if (status === 'Passed') return 'chip chip-good';
    if (status === 'Flagged') return 'chip chip-danger';
    return 'chip chip-neutral';
  }

  protected verifiedDims(a: YardArtifactView): string | null {
    const v = a.verifiedPallets;
    if (!v) return null;
    const dims = [v.lengthInches, v.widthInches, v.heightInches];
    if (dims.some((d) => d === null || d === undefined)) return null;
    return `${dims[0]}×${dims[1]}×${dims[2]} in`;
  }

  protected place(p: LtlPlace | null): string {
    if (!p) return '—';
    return p.label ?? ([p.city, p.state, p.zip].filter(Boolean).join(', ') || '—');
  }

  protected formatCurrency(value: number | null | undefined): string {
    if (value === null || value === undefined) return '—';
    return `$${value.toLocaleString(undefined, { maximumFractionDigits: 0 })}`;
  }

  protected formatNumber(value: number | null | undefined, suffix = ''): string {
    if (value === null || value === undefined) return '—';
    return `${value.toLocaleString(undefined, { maximumFractionDigits: 0 })}${suffix}`;
  }

  /** Weight is optional on many Alvys loads — surface "unknown", never 0. */
  protected weightLabel(load: LtlLoadSummary): string {
    return load.weightLbs === null || load.weightLbs === undefined
      ? 'Unknown'
      : `${load.weightLbs.toLocaleString(undefined, { maximumFractionDigits: 0 })} lb`;
  }

  protected badgeClass(badge: string): string {
    if (badge === 'ReadyToBill') return 'chip chip-good';
    if (badge === 'AlreadyInvoiced') return 'chip chip-neutral';
    if (badge === 'ExceptionBlockingBilling') return 'chip chip-danger';
    return 'chip chip-warn';
  }

  /** Chip styling for an accessorial-review candidate status: Likely = warn, CannotEvaluate = neutral. */
  protected candidateChipClass(status: string): string {
    if (status === 'Likely') return 'chip chip-warn';
    if (status === 'CannotEvaluate') return 'chip chip-neutral';
    return 'chip chip-muted';
  }
}

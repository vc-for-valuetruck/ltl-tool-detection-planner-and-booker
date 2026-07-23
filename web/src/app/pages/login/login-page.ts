import { CommonModule } from '@angular/common';
import { Component, OnInit, inject } from '@angular/core';
import { Router } from '@angular/router';
import { MsalService } from '@azure/msal-angular';
import { RUNTIME_CONFIG, isAuthConfigured } from '../../runtime-config';

/**
 * Branded sign-in screen shown before Microsoft Entra login for the LTL Tool.
 *
 * Rendered full-screen (the app shell hides its header on `/login`). Unauthenticated
 * users are routed here by `authGuard` when Entra is configured; they click
 * "Sign in with Microsoft" to start the interactive redirect. The Value Truck mark is
 * inlined as a white-on-transparent data URI so no asset-pipeline change is required.
 *
 * Matches the FreightDNA login page 1:1 in structure and styling — same aura + grid
 * background, same brandmark, same Microsoft-button treatment — rebranded for LTL.
 * Unlike FreightDNA there is no "review mode" click-through: if Entra runtime
 * config is missing the Sign in button stays disabled and a status banner surfaces
 * the config gap so it gets flagged instead of silently falling through.
 */
@Component({
  selector: 'app-login-page',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="login">
      <div class="login__aura" aria-hidden="true"></div>
      <div class="login__grid" aria-hidden="true"></div>

      <main class="login__card" role="main">
        <div class="brandmark">
          <img class="brandmark__logo" [src]="logoSrc" alt="Value Truck" width="156" height="70" />
          <span class="brandmark__divider" aria-hidden="true"></span>
          <div class="brandmark__copy">
            <strong>LTL Tool</strong>
            <small>LTL Planner · Booker</small>
          </div>
        </div>

        <h1 class="login__title">Sign in to the LTL Tool</h1>
        <p class="login__subtitle">
          The Value Truck LTL detection, planner, and booker console. Use your company
          Microsoft account to continue.
        </p>

        <button
          type="button"
          class="ms-button"
          [disabled]="signingIn || !authConfigured"
          (click)="signIn()"
        >
          <svg class="ms-button__logo" viewBox="0 0 23 23" width="20" height="20" aria-hidden="true">
            <path fill="#f25022" d="M1 1h10v10H1z" />
            <path fill="#7fba00" d="M12 1h10v10H12z" />
            <path fill="#00a4ef" d="M1 12h10v10H1z" />
            <path fill="#ffb900" d="M12 12h10v10H12z" />
          </svg>
          <span>{{ signingIn ? 'Redirecting to Microsoft…' : 'Sign in with Microsoft' }}</span>
        </button>

        <p class="login__notice" *ngIf="!authConfigured" role="status">
          Microsoft Entra sign-in is not configured for this environment.
          <code>RUNTIME_TENANT_ID</code> and <code>RUNTIME_WEB_CLIENT_ID</code> must be
          injected before this app can authenticate. Please contact an administrator.
        </p>

        <p class="login__secure">
          <svg viewBox="0 0 24 24" width="14" height="14" aria-hidden="true">
            <path fill="currentColor" d="M12 1 4 4v6c0 5 3.4 9.4 8 11 4.6-1.6 8-6 8-11V4l-8-3zm0 10.9h6c-.5 3.7-2.9 7-6 8.2V12H6V5.3l6-2.2v8.8z" />
          </svg>
          Secured by Microsoft Entra ID · Value Truck company access only
        </p>
      </main>

      <footer class="login__footer">
        <span>© {{ year }} Value Truck + Value Logistics</span>
        <span aria-hidden="true">·</span>
        <span>LTL Tool</span>
      </footer>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .login {
      position: relative;
      min-height: 100vh;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      gap: 2.5rem;
      padding: 2rem 1.25rem;
      background: radial-gradient(1200px 700px at 50% -10%, #14161c 0%, #0a0b0d 42%, #050506 100%);
      color: #f5f7fa;
      overflow: hidden;
      font-family: 'Inter', system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif;
    }
    .login__aura {
      position: absolute;
      top: -22%;
      left: 50%;
      width: 780px;
      height: 780px;
      transform: translateX(-50%);
      background: radial-gradient(circle, rgba(245,158,11,.18) 0%, rgba(245,158,11,.06) 34%, transparent 66%);
      filter: blur(6px);
      pointer-events: none;
    }
    .login__grid {
      position: absolute;
      inset: 0;
      background-image:
        linear-gradient(rgba(255,255,255,.035) 1px, transparent 1px),
        linear-gradient(90deg, rgba(255,255,255,.035) 1px, transparent 1px);
      background-size: 46px 46px;
      mask-image: radial-gradient(700px 520px at 50% 30%, #000 0%, transparent 78%);
      -webkit-mask-image: radial-gradient(700px 520px at 50% 30%, #000 0%, transparent 78%);
      pointer-events: none;
    }
    .login__card {
      position: relative;
      z-index: 1;
      width: 100%;
      max-width: 428px;
      display: flex;
      flex-direction: column;
      align-items: center;
      text-align: center;
      padding: 3rem 2.4rem 2.6rem;
      border-radius: 22px;
      background: linear-gradient(180deg, rgba(24,26,32,.86) 0%, rgba(14,15,18,.92) 100%);
      border: 1px solid rgba(255,255,255,.09);
      box-shadow: 0 40px 90px rgba(0,0,0,.6), inset 0 1px 0 rgba(255,255,255,.06);
      backdrop-filter: blur(14px);
    }
    .brandmark {
      display: flex;
      align-items: center;
      gap: 1rem;
      margin-bottom: 2rem;
    }
    .brandmark__logo {
      height: 62px;
      width: auto;
      object-fit: contain;
      filter: drop-shadow(0 6px 18px rgba(0,0,0,.5));
    }
    .brandmark__divider {
      width: 1px;
      height: 42px;
      background: linear-gradient(180deg, transparent, rgba(255,255,255,.28), transparent);
    }
    .brandmark__copy { display: flex; flex-direction: column; line-height: 1.15; text-align: left; }
    .brandmark__copy strong { font-size: 1.12rem; font-weight: 800; letter-spacing: -.02em; color: #fff; }
    .brandmark__copy small {
      margin-top: .28rem;
      font-size: .62rem;
      font-weight: 700;
      letter-spacing: .18em;
      text-transform: uppercase;
      color: #f59e0b;
    }
    .login__title {
      margin: 0 0 .6rem;
      font-size: 1.62rem;
      font-weight: 800;
      letter-spacing: -.02em;
      color: #fff;
    }
    .login__subtitle {
      margin: 0 0 2rem;
      max-width: 33ch;
      font-size: .93rem;
      line-height: 1.55;
      color: #a7adba;
    }
    .ms-button {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      gap: .72rem;
      width: 100%;
      min-height: 52px;
      padding: 0 1.2rem;
      border: 1px solid rgba(255,255,255,.14);
      border-radius: 12px;
      background: #ffffff;
      color: #1b1b1f;
      font-size: .98rem;
      font-weight: 700;
      cursor: pointer;
      transition: transform .14s ease, box-shadow .14s ease, background .14s ease;
      box-shadow: 0 12px 30px rgba(0,0,0,.35);
    }
    .ms-button:hover:not(:disabled) { transform: translateY(-1px); background: #f4f5f7; box-shadow: 0 16px 38px rgba(0,0,0,.45); }
    .ms-button:active:not(:disabled) { transform: translateY(0); }
    .ms-button:disabled { opacity: .7; cursor: not-allowed; }
    .ms-button:focus-visible { outline: 3px solid rgba(245,158,11,.6); outline-offset: 3px; }
    .ms-button__logo { flex: 0 0 auto; }
    .login__notice {
      margin: 1.5rem 0 0;
      padding: .85rem 1rem;
      width: 100%;
      border-radius: 10px;
      background: rgba(239,68,68,.1);
      border: 1px solid rgba(239,68,68,.32);
      font-size: .8rem;
      line-height: 1.5;
      color: #fecaca;
      text-align: left;
    }
    .login__notice code {
      font-family: 'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, monospace;
      font-size: .74rem;
      background: rgba(0,0,0,.35);
      padding: .05rem .35rem;
      border-radius: 4px;
      color: #fde68a;
    }
    .login__secure {
      display: inline-flex;
      align-items: center;
      gap: .5rem;
      margin: 1.9rem 0 0;
      font-size: .72rem;
      letter-spacing: .01em;
      color: #6f7686;
    }
    .login__secure svg { color: #4ade80; flex: 0 0 auto; }
    .login__footer {
      position: relative;
      z-index: 1;
      display: flex;
      align-items: center;
      gap: .55rem;
      font-size: .72rem;
      color: #565c69;
    }
    @media (max-width: 480px) {
      .login { padding: 1.5rem 1rem; gap: 1.75rem; }
      .login__card { padding: 2.4rem 1.5rem 2rem; border-radius: 18px; }
      .login__title { font-size: 1.42rem; }
      .brandmark__logo { height: 54px; }
    }
  `],
})
export class LoginPage implements OnInit {
  private readonly msal = inject(MsalService);
  private readonly router = inject(Router);
  private readonly config = inject(RUNTIME_CONFIG);

  readonly year = new Date().getFullYear();
  readonly logoSrc = VT_LOGO_DATA_URI;
  readonly authConfigured = isAuthConfigured(this.config);
  signingIn = false;

  ngOnInit(): void {
    // If a real Entra session already exists, don't strand the user on the login
    // screen — send them into the LTL console.
    if (this.authConfigured && this.msal.instance.getAllAccounts().length > 0) {
      void this.router.navigateByUrl('/ltl', { replaceUrl: true });
    }
  }

  signIn(): void {
    if (this.signingIn || !this.authConfigured) {
      return;
    }
    this.signingIn = true;
    const scopes = this.config.apiScope ? [this.config.apiScope] : [];
    this.msal.loginRedirect({ scopes });
  }
}

const VT_LOGO_DATA_URI = 'data:image/png;base64,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';

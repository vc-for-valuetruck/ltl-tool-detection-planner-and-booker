import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { RUNTIME_CONFIG, isAuthConfigured } from './runtime-config';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [RouterOutlet],
  templateUrl: './app.html',
  styleUrls: ['./app.css'],
})
export class App {
  private readonly config = inject(RUNTIME_CONFIG);
  protected readonly authConfigured = isAuthConfigured(this.config);
  /** Demo-mode identity shown top-right when Entra auth isn't configured for this environment. */
  protected readonly demoEmail = 'demo@valuetruck.com';
}

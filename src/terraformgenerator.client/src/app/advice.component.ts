import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Recommendation } from './models';
import { SketchStore } from './sketch.store';

const CATEGORY_LABELS: Record<Recommendation['category'], string> = {
  security: 'Security',
  reliability: 'Reliability',
  cost: 'Cost',
  performance: 'Performance',
  operations: 'Operations',
};

// The AI reviews the drawing; each recommendation with a change becomes a proposal to approve or discard.
@Component({
  selector: 'app-advice',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2>AI review</h2>
    <p class="hint">The AI looks at your drawing and suggests what to improve for security, reliability and cost. Applying one shows it as a proposal first; nothing changes until you approve.</p>
    @if (!store.aiStatus()?.enabled) {
      <p class="message warning">{{ store.aiStatus()?.problem ?? 'The AI is not available.' }}</p>
    } @else {
      <div class="actions">
        <button type="button" class="primary" [disabled]="store.recommending() || store.sketch().nodes.length === 0" (click)="store.recommend()">
          {{ store.recommending() ? 'Reviewing…' : store.recommendations() ? 'Review again' : 'Review my design' }}
        </button>
      </div>
      @if (store.sketch().nodes.length === 0) {
        <p class="hint small">Draw something first, or describe it with AI.</p>
      }
    }

    @if (store.recommendationError(); as error) {
      <p class="message error" role="alert">{{ error }}</p>
    }

    @if (store.recommendations(); as result) {
      @if (result.summary) {
        <p class="summary">{{ result.summary }}</p>
      }
      <ol class="recommendations">
        @for (item of result.recommendations; track $index) {
          <li [class]="item.priority">
            <div class="head">
              <span class="tag" [class]="item.category">{{ category(item) }}</span>
              <span class="priority">{{ item.priority }}</span>
            </div>
            <strong>{{ item.title }}</strong>
            <p>{{ item.why }}</p>
            @if (item.change) {
              <button type="button" [disabled]="store.proposing()" (click)="apply(item, $index)">
                {{ store.proposing() && applying() === $index ? 'Preparing…' : 'Apply as a proposal' }}
              </button>
            } @else {
              <p class="hint small">Advice only: Terraform Generator cannot draw this.</p>
            }
          </li>
        }
      </ol>
    }
  `,
  styleUrl: './panel.css',
})
export class AdviceComponent {
  protected readonly store = inject(SketchStore);
  protected readonly applying = signal<number | null>(null);

  protected category(item: Recommendation): string {
    return CATEGORY_LABELS[item.category] ?? item.category;
  }

  protected async apply(item: Recommendation, index: number): Promise<void> {
    this.applying.set(index);
    await this.store.applyRecommendation(item);
    this.applying.set(null);
  }
}

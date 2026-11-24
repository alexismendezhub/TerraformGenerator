import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { NODE_HEIGHT, NODE_WIDTH, SketchNode } from './models';
import { SketchStore } from './sketch.store';

interface Point {
  x: number;
  y: number;
}

// Where the line from the center of a box toward a point leaves the box.
function borderPoint(node: SketchNode, toward: Point): Point {
  const center = { x: node.x + NODE_WIDTH / 2, y: node.y + NODE_HEIGHT / 2 };
  const dx = toward.x - center.x;
  const dy = toward.y - center.y;
  if (dx === 0 && dy === 0) {
    return center;
  }

  const scale = Math.min(
    dx === 0 ? Infinity : NODE_WIDTH / 2 / Math.abs(dx),
    dy === 0 ? Infinity : NODE_HEIGHT / 2 / Math.abs(dy),
  );
  return { x: center.x + dx * scale, y: center.y + dy * scale };
}

function centerOf(node: SketchNode): Point {
  return { x: node.x + NODE_WIDTH / 2, y: node.y + NODE_HEIGHT / 2 };
}

@Component({
  selector: 'app-canvas',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg #surface class="surface" [attr.width]="size().width" [attr.height]="size().height"
         (pointermove)="onMove($event)" (pointerup)="onUp()" (pointerleave)="onUp()" (pointerdown)="onBackground($event)"
         role="application" aria-label="Architecture drawing">
      <defs>
        <marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse">
          <path d="M 0 0 L 10 5 L 0 10 z" class="arrow-head" />
        </marker>
        <marker id="arrow-selected" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse">
          <path d="M 0 0 L 10 5 L 0 10 z" class="arrow-head selected" />
        </marker>
        <pattern id="grid" width="20" height="20" patternUnits="userSpaceOnUse">
          <circle cx="1" cy="1" r="1" class="grid-dot" />
        </pattern>
      </defs>
      <rect class="grid" width="100%" height="100%" fill="url(#grid)" />

      @for (line of lines(); track line.id) {
        <g class="edge" [class.selected]="line.selected" [class.invalid]="line.invalid" (pointerdown)="selectEdge($event, line.id)">
          <line class="hit" [attr.x1]="line.x1" [attr.y1]="line.y1" [attr.x2]="line.x2" [attr.y2]="line.y2" />
          <line class="stroke" [attr.x1]="line.x1" [attr.y1]="line.y1" [attr.x2]="line.x2" [attr.y2]="line.y2"
                [attr.marker-end]="line.selected ? 'url(#arrow-selected)' : 'url(#arrow)'" />
        </g>
      }

      @if (draft(); as draft) {
        <line class="draft" [attr.x1]="draft.x1" [attr.y1]="draft.y1" [attr.x2]="draft.x2" [attr.y2]="draft.y2" marker-end="url(#arrow)" />
      }

      @for (node of store.displayed().nodes; track node.id) {
        <g class="node" [attr.transform]="'translate(' + node.x + ',' + node.y + ')'" [attr.data-kind]="node.kind"
           [class.selected]="isSelected(node)" [class.has-error]="nodesWithErrors().has(node.id)"
           [class.added]="marks().get(node.id) === 'added'" [class.changed]="marks().get(node.id) === 'changed'"
           [class.target]="connecting() !== null && canTarget(node)" [class.dim]="connecting() !== null && !canTarget(node) && connecting() !== node.id"
           (pointerdown)="startDrag($event, node)" (pointerup)="finishConnect($event, node)">
          <rect class="box" [attr.width]="nodeWidth" [attr.height]="nodeHeight" rx="10" />
          <rect class="band" width="44" [attr.height]="nodeHeight" rx="10" />
          <rect class="band-fill" x="34" width="10" [attr.height]="nodeHeight" />
          <text class="short" x="22" [attr.y]="nodeHeight / 2 + 4" text-anchor="middle">{{ store.shortOf(node.kind) }}</text>
          <text class="name" x="56" y="26">{{ node.name }}</text>
          <text class="kind" x="56" y="44">{{ store.labelOf(node.kind) }}</text>
          @if (reviewed().has(node.id)) {
            <g class="review-flag" [attr.transform]="'translate(' + (nodeWidth - 12) + ',-8)'">
              <circle r="9" /><text y="4" text-anchor="middle">!</text>
              <title>Needs your review</title>
            </g>
          }
          <circle class="handle" [class.hidden]="store.previewing()" [attr.cx]="nodeWidth" [attr.cy]="nodeHeight / 2" r="7" (pointerdown)="startConnect($event, node)">
            <title>Drag to another box to connect</title>
          </circle>
        </g>
      }
    </svg>
    @if (store.displayed().settings.platform === 'azure') {
      <button type="button" class="resource-group" (click)="store.selection.set(null)"
              title="Every resource goes in this resource group. Click to change it in the project settings.">
        <span class="label">Resource group</span> {{ resourceGroup().name }} <span class="mode">{{ resourceGroup().existing ? 'existing' : 'new' }}</span>
      </button>
    }
    @if (store.displayed().nodes.length === 0) {
      <div class="empty">
        <p><strong>Start your drawing.</strong></p>
        <p>Pick a resource on the left, then drag from the dot on its right edge to another box to connect them.</p>
        <button type="button" (click)="store.loadExample()">Open the example</button>
      </div>
    }
  `,
  styleUrl: './canvas.component.css',
})
export class CanvasComponent {
  protected readonly store = inject(SketchStore);
  protected readonly nodeWidth = NODE_WIDTH;
  protected readonly nodeHeight = NODE_HEIGHT;
  private readonly surface = viewChild.required<ElementRef<SVGSVGElement>>('surface');

  private drag: { id: string; dx: number; dy: number } | null = null;
  protected readonly connecting = signal<string | null>(null);
  private readonly pointer = signal<Point>({ x: 0, y: 0 });

  // The resource group every box goes in, as the drawing (or the proposal on screen) says.
  protected readonly resourceGroup = computed(() => {
    const settings = this.store.displayed().settings;
    return {
      name: settings.options?.['resourceGroupName'] || `${settings.name}-${settings.environment}-rg`,
      existing: settings.options?.['resourceGroup'] === 'existing',
    };
  });

  protected readonly size = computed(() => {
    const nodes = this.store.displayed().nodes;
    return {
      width: Math.max(1600, ...nodes.map(node => node.x + NODE_WIDTH + 200)),
      height: Math.max(1000, ...nodes.map(node => node.y + NODE_HEIGHT + 200)),
    };
  });

  // While an AI proposal is shown, its own checks and marks apply.
  private readonly shownMessages = computed(() => this.store.proposal()?.messages ?? this.store.messages());

  protected readonly nodesWithErrors = computed(
    () => new Set(this.shownMessages().filter(message => message.level === 'error' && message.nodeId).map(message => message.nodeId)),
  );

  protected readonly marks = computed(() => new Map(
    (this.store.proposal()?.changes ?? []).filter(change => change.nodeId && change.type !== 'removed').map(change => [change.nodeId!, change.type] as const),
  ));

  protected readonly reviewed = computed(() => new Set((this.store.proposal()?.review ?? []).map(item => item.nodeId).filter(id => !!id)));

  protected readonly lines = computed(() => {
    const sketch = this.store.displayed();
    const byId = new Map(sketch.nodes.map(node => [node.id, node] as const));
    const selection = this.store.selection();
    const invalid = new Set(this.shownMessages().filter(message => message.edgeId).map(message => message.edgeId));
    return sketch.edges.flatMap(edge => {
      const from = byId.get(edge.from);
      const to = byId.get(edge.to);
      if (!from || !to) {
        return [];
      }

      const start = borderPoint(from, centerOf(to));
      const end = borderPoint(to, centerOf(from));
      return [{
        id: edge.id, x1: start.x, y1: start.y, x2: end.x, y2: end.y,
        selected: selection?.type === 'edge' && selection.id === edge.id, invalid: invalid.has(edge.id),
      }];
    });
  });

  protected readonly draft = computed(() => {
    const id = this.connecting();
    const from = id ? this.store.sketch().nodes.find(node => node.id === id) : undefined;
    if (!from) {
      return null;
    }

    const pointer = this.pointer();
    const start = borderPoint(from, pointer);
    return { x1: start.x, y1: start.y, x2: pointer.x, y2: pointer.y };
  });

  protected isSelected(node: SketchNode): boolean {
    const selection = this.store.selection();
    return selection?.type === 'node' && selection.id === node.id;
  }

  protected canTarget(node: SketchNode): boolean {
    const from = this.store.sketch().nodes.find(item => item.id === this.connecting());
    return !!from && this.store.canConnect(from, node);
  }

  private toSvg(event: PointerEvent): Point {
    const box = this.surface().nativeElement.getBoundingClientRect();
    return { x: event.clientX - box.left, y: event.clientY - box.top };
  }

  protected onBackground(event: PointerEvent): void {
    if (event.target === this.surface().nativeElement || (event.target as Element).classList.contains('grid')) {
      this.store.selection.set(null);
    }
  }

  protected startDrag(event: PointerEvent, node: SketchNode): void {
    event.stopPropagation();
    if (this.store.previewing()) {
      return;
    }

    const point = this.toSvg(event);
    this.drag = { id: node.id, dx: point.x - node.x, dy: point.y - node.y };
    this.store.selection.set({ type: 'node', id: node.id });
  }

  protected startConnect(event: PointerEvent, node: SketchNode): void {
    event.stopPropagation();
    if (this.store.previewing()) {
      return;
    }

    this.drag = null;
    this.pointer.set(this.toSvg(event));
    this.connecting.set(node.id);
  }

  protected finishConnect(event: PointerEvent, node: SketchNode): void {
    const fromId = this.connecting();
    if (!fromId) {
      return;
    }

    event.stopPropagation();
    const from = this.store.sketch().nodes.find(item => item.id === fromId);
    if (from) {
      this.store.connect(from, node);
    }

    this.connecting.set(null);
  }

  protected selectEdge(event: PointerEvent, id: string): void {
    event.stopPropagation();
    if (this.store.previewing()) {
      return;
    }

    this.store.selection.set({ type: 'edge', id });
  }

  protected onMove(event: PointerEvent): void {
    const point = this.toSvg(event);
    if (this.connecting()) {
      this.pointer.set(point);
    } else if (this.drag) {
      this.store.moveNode(this.drag.id, point.x - this.drag.dx, point.y - this.drag.dy);
    }
  }

  protected onUp(): void {
    this.drag = null;
    this.connecting.set(null);
  }
}

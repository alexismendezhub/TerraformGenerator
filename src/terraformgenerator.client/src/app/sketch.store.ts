import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  AiStatus, CloudPlatform, GenerationResult, NODE_HEIGHT, NODE_WIDTH, REFERENCE_PREFIX, Recommendation, RecommendationResult, ResourceKind,
  ResourceSchema, ResourceSummary, SchemaStatus, Selection, Sketch, SketchCatalog, SketchEdge, SketchMessage, SketchNode, SketchProposal, SketchSettings,
  TerraformCheck, isGeneric, typeOf,
  ExampleName, PalettePreferences,
} from './models';

const STORAGE_KEY = 'terraformgenerator.drawing';
const PREFERENCES_KEY = 'terraformgenerator.palette.';

// Reads the saved palette of a cloud; anything missing or malformed gives an empty palette.
export function readPreferences(platform: string): PalettePreferences {
  try {
    const saved = JSON.parse(localStorage.getItem(PREFERENCES_KEY + platform) ?? 'null') as Partial<PalettePreferences> | null;
    const pins = Array.isArray(saved?.pins) ? saved.pins.filter((pin): pin is string => typeof pin === 'string') : [];
    const sections: Record<string, string> = {};
    if (saved?.sections && typeof saved.sections === 'object') {
      for (const [key, value] of Object.entries(saved.sections)) {
        if (typeof value === 'string' && value.trim()) {
          sections[key] = value.trim().slice(0, 40);
        }
      }
    }
    return { pins: [...new Set(pins)].slice(0, 200), sections };
  } catch {
    return { pins: [], sections: {} };
  }
}

const DEFAULT_NAMES: Record<string, string> = {
  webApp: 'app',
  containerApp: 'api',
  functionApp: 'jobs',
  staticWebApp: 'site',
  sqlDatabase: 'db',
  postgres: 'pg',
  cosmosDb: 'docs',
  storage: 'files',
  redis: 'cache',
  serviceBus: 'queue',
  keyVault: 'vault',
  containerRegistry: 'images',
  frontDoor: 'edge',
  dnsZone: 'domain',
  aiFoundry: 'ai',
  speech: 'voice',
};

function emptySketch(): Sketch {
  return { settings: { platform: 'azure', name: 'myapp', environment: 'dev', region: 'eastus2', monitoring: true, options: {} }, nodes: [], edges: [] };
}

// aws_s3_bucket => "S3 Bucket" and "S3"; the same idea as the server's summaries.
const UPPER = new Set(['s3', 'ec2', 'ecs', 'eks', 'rds', 'sqs', 'sns', 'iam', 'vpc', 'kms', 'acm', 'dns', 'ip', 'api', 'sql', 'id', 'db', 'ai', 'vm', 'nat',
  'cdn', 'waf', 'efs', 'ebs', 'lb', 'ses', 'ssm', 'gke', 'v2']);
// Service names written their own way (the same as Platforms.Special on the server).
const SPECIAL: Record<string, string> = {
  dynamodb: 'DynamoDB', cloudfront: 'CloudFront', cloudwatch: 'CloudWatch', elasticache: 'ElastiCache', opensearch: 'OpenSearch',
  bigquery: 'BigQuery', pubsub: 'PubSub', cosmosdb: 'CosmosDB', apigatewayv2: 'APIGatewayV2',
};
const PREFIXES = ['azurerm_', 'aws_', 'google_'];

export function summarize(type: string): { title: string; short: string } {
  const prefix = PREFIXES.find(item => type.startsWith(item)) ?? '';
  const words = type.slice(prefix.length).split('_').filter(word => word.length > 0);
  const title = words.map(word => SPECIAL[word] ?? (UPPER.has(word) ? word.toUpperCase() : word[0].toUpperCase() + word.slice(1))).join(' ');
  const short = SPECIAL[words[0]] ? SPECIAL[words[0]].replace(/[^A-Z]/g, '').slice(0, 4)
    : UPPER.has(words[0]) ? words[0].toUpperCase().slice(0, 4)
    : words.length === 1 ? words[0].slice(0, 3).toUpperCase() : words.slice(0, 3).map(word => word[0].toUpperCase()).join('');
  return { title, short };
}

// A box name from a resource type: aws_s3_bucket => bucket, google_cloud_run_v2_service => service.
function genericName(type: string): string {
  const words = type.split('_').filter(word => /^[a-z][a-z0-9]*$/.test(word) && word.length > 1);
  const word = words[words.length - 1] ?? 'item';
  return word.slice(0, 18);
}

function newId(prefix: string): string {
  return `${prefix}${Math.random().toString(36).slice(2, 9)}`;
}

// The drawing, the selection and the generated Terraform. Every change regenerates the code shortly after.
@Injectable({ providedIn: 'root' })
export class SketchStore {
  private readonly http = inject(HttpClient);
  private generation = 0;
  private timer: ReturnType<typeof setTimeout> | undefined;

  readonly catalog = signal<SketchCatalog | null>(null);
  readonly sketch = signal<Sketch>(this.restore());
  readonly selection = signal<Selection>(null);
  readonly result = signal<GenerationResult | null>(null);
  readonly generating = signal(false);
  readonly check = signal<TerraformCheck | null>(null);
  readonly checking = signal(false);
  readonly serverError = signal<string | null>(null);

  // AI proposal: shown on the canvas instead of the drawing until the person approves or discards it.
  readonly aiStatus = signal<AiStatus | null>(null);
  readonly proposal = signal<SketchProposal | null>(null);
  readonly proposing = signal(false);
  readonly proposalError = signal<string | null>(null);
  readonly displayed = computed(() => this.proposal()?.sketch ?? this.sketch());
  readonly previewing = computed(() => this.proposal() !== null);
  // Counts approvals, so the screen can show the code right after one.
  readonly approvals = signal(0);

  // AI review of the drawing: recommendations, each with a change that can be proposed.
  readonly recommendations = signal<RecommendationResult | null>(null);
  readonly recommending = signal(false);
  readonly recommendationError = signal<string | null>(null);

  readonly kinds = computed(() => new Map((this.catalog()?.kinds ?? []).map(kind => [kind.kind, kind] as const)));

  // The clouds, and the provider lists: resources found by the search and the fields of each resource used.
  readonly platforms = signal<CloudPlatform[]>([]);
  readonly platform = computed(() => this.platforms().find(item => item.id === (this.sketch().settings.platform ?? 'azure')) ?? null);
  readonly schemas = signal<Record<string, ResourceSchema>>({});
  // The palette of the drawing's cloud as the person left it (kept by the server on this computer).
  readonly preferences = signal<PalettePreferences>({ pins: [], sections: {} });
  private preferencesPlatform = '';
  readonly schemaLoading = signal(false);
  readonly schemaStatus = signal<SchemaStatus | null>(null);
  readonly schemaRefreshing = signal(false);
  readonly messages = computed<SketchMessage[]>(() => this.result()?.messages ?? []);
  readonly errorCount = computed(() => this.messages().filter(message => message.level === 'error').length);
  readonly selectedNode = computed(() => {
    const selection = this.selection();
    return selection?.type === 'node' ? this.sketch().nodes.find(node => node.id === selection.id) ?? null : null;
  });
  readonly selectedEdge = computed(() => {
    const selection = this.selection();
    return selection?.type === 'edge' ? this.sketch().edges.find(edge => edge.id === selection.id) ?? null : null;
  });

  constructor() {
    void this.loadCatalog();
    void this.loadAiStatus();
    void this.loadPlatforms();
    effect(() => {
      const platform = this.sketch().settings.platform ?? 'azure';
      if (platform !== this.preferencesPlatform) {
        this.preferencesPlatform = platform;
        this.loadPreferences(platform);
      }
    });
    effect(() => {
      const sketch = this.sketch();
      try {
        localStorage.setItem(STORAGE_KEY, JSON.stringify(sketch));
      } catch {
        // Private windows may refuse storage; the drawing still works.
      }

      clearTimeout(this.timer);
      this.timer = setTimeout(() => void this.generate(sketch), 250);
    });
  }

  kind(kind: string): ResourceKind | undefined {
    return this.kinds().get(kind);
  }

  // The short code and the title a box shows, for catalog boxes and provider boxes alike.
  shortOf(kind: string): string {
    return isGeneric(kind) ? summarize(typeOf(kind)).short : this.kind(kind)?.short ?? '?';
  }

  labelOf(kind: string): string {
    return isGeneric(kind) ? summarize(typeOf(kind)).title : this.kind(kind)?.label ?? kind;
  }

  canConnect(from: SketchNode, to: SketchNode): boolean {
    if (from.id === to.id) {
      return false;
    }

    // A provider box can point to any box; the arrow then asks which of its fields holds the other box.
    if (isGeneric(from.kind)) {
      return !this.sketch().edges.some(edge => edge.from === from.id && edge.to === to.id);
    }

    const allowed = (this.catalog()?.connections ?? []).some(item => item.from === from.kind && item.to === to.kind);
    return allowed && !this.sketch().edges.some(edge => edge.from === from.id && edge.to === to.id);
  }

  connectionText(edge: SketchEdge): string {
    const nodes = this.sketch().nodes;
    const from = nodes.find(node => node.id === edge.from);
    const to = nodes.find(node => node.id === edge.to);
    return this.catalog()?.connections.find(item => item.from === from?.kind && item.to === to?.kind)?.description ?? '';
  }

  // Changes ---------------------------------------------------------------------------------------------------------

  private update(change: (sketch: Sketch) => Sketch): void {
    this.sketch.update(change);
    this.check.set(null);
  }

  addNode(kind: string): void {
    const sketch = this.sketch();
    const base = DEFAULT_NAMES[kind] ?? (isGeneric(kind) ? genericName(typeOf(kind)) : 'item');
    let name = base;
    for (let index = 2; sketch.nodes.some(node => node.name === name); index++) {
      name = `${base}${index}`;
    }

    const { x, y } = freeSpot(sketch.nodes);
    const node: SketchNode = { id: newId('n'), kind, name, x, y, props: {} };
    this.update(current => ({ ...current, nodes: [...current.nodes, node] }));
    this.selection.set({ type: 'node', id: node.id });
  }

  moveNode(id: string, x: number, y: number): void {
    this.sketch.update(current => ({
      ...current,
      nodes: current.nodes.map(node => (node.id === id ? { ...node, x: Math.max(0, Math.round(x)), y: Math.max(0, Math.round(y)) } : node)),
    }));
  }

  renameNode(id: string, name: string): void {
    this.update(current => ({ ...current, nodes: current.nodes.map(node => (node.id === id ? { ...node, name } : node)) }));
  }

  setProp(id: string, key: string, value: string): void {
    this.update(current => ({
      ...current,
      nodes: current.nodes.map(node => (node.id === id ? { ...node, props: { ...node.props, [key]: value } } : node)),
    }));
  }

  connect(from: SketchNode, to: SketchNode): void {
    if (!this.canConnect(from, to)) {
      return;
    }

    const edge: SketchEdge = { id: newId('e'), from: from.id, to: to.id };
    this.update(current => ({ ...current, edges: [...current.edges, edge] }));
    this.selection.set({ type: 'edge', id: edge.id });
  }

  removeSelected(): void {
    const selection = this.selection();
    if (selection?.type === 'node') {
      this.update(current => ({
        ...current,
        nodes: current.nodes.filter(node => node.id !== selection.id),
        edges: current.edges.filter(edge => edge.from !== selection.id && edge.to !== selection.id),
      }));
    } else if (selection?.type === 'edge') {
      this.update(current => ({ ...current, edges: current.edges.filter(edge => edge.id !== selection.id) }));
    }

    this.selection.set(null);
  }

  setSettings(change: Partial<SketchSettings>): void {
    this.update(current => ({ ...current, settings: { ...current.settings, ...change } }));
  }

  // Output, state, pipeline and budget (catalog settings); missing ones take the default.
  setting(key: string): string {
    return this.sketch().settings.options?.[key] ?? this.catalog()?.settings.find(item => item.key === key)?.default ?? '';
  }

  // Any project value by key, the four fixed ones included (for showWhen).
  settingValue(key: string): string {
    const settings = this.sketch().settings;
    switch (key) {
      case 'name': return settings.name;
      case 'environment': return settings.environment;
      case 'region': return settings.region;
      case 'monitoring': return settings.monitoring ? 'true' : 'false';
      default: return this.setting(key);
    }
  }

  setOption(key: string, value: string): void {
    this.update(current => ({ ...current, settings: { ...current.settings, options: { ...(current.settings.options ?? {}), [key]: value } } }));
  }

  replace(sketch: Sketch): void {
    this.update(() => sketch);
    this.selection.set(null);
  }

  clear(): void {
    this.replace(emptySketch());
  }

  async loadExample(name: ExampleName = 'simple'): Promise<void> {
    this.replace(await firstValueFrom(this.http.get<Sketch>(`/api/example?name=${name}`)));
  }

  // Clouds and provider lists ---------------------------------------------------------------------------------------

  private async loadPlatforms(): Promise<void> {
    try {
      this.platforms.set(await firstValueFrom(this.http.get<CloudPlatform[]>('/api/platforms')));
    } catch {
      // The catalog load reports a server that does not answer.
    }
  }

  // Another cloud means another drawing: its boxes would not exist there.
  setPlatform(id: string): void {
    const platform = this.platforms().find(item => item.id === id);
    if (!platform) {
      return;
    }

    this.update(current => ({
      settings: { ...current.settings, platform: id, region: platform.regions[0]?.value ?? current.settings.region, options: {} },
      nodes: [],
      edges: [],
    }));
    this.selection.set(null);
  }

  // The palette of each cloud (pins and section names) is kept in this browser, like the drawing.
  private loadPreferences(platform: string): void {
    this.preferences.set(readPreferences(platform));
  }

  private savePreferences(preferences: PalettePreferences): void {
    this.preferences.set(preferences);
    try {
      localStorage.setItem(PREFERENCES_KEY + this.preferencesPlatform, JSON.stringify(preferences));
    } catch {
      // Storage full or blocked: the palette still works for this session.
    }
  }

  isPinned(type: string): boolean {
    return this.preferences().pins.includes(type);
  }

  togglePin(type: string): void {
    const current = this.preferences();
    this.savePreferences({ ...current, pins: this.isPinned(type) ? current.pins.filter(pin => pin !== type) : [...current.pins, type] });
  }

  // A section's name as the person called it; an empty name goes back to the usual one.
  sectionName(group: string): string {
    return this.preferences().sections[group] ?? group;
  }

  renameSection(group: string, name: string): void {
    const sections = { ...this.preferences().sections };
    const trimmed = name.trim().slice(0, 40);
    if (trimmed.length === 0 || trimmed === group) {
      delete sections[group];
    } else {
      sections[group] = trimmed;
    }

    this.savePreferences({ ...this.preferences(), sections });
  }

  async searchResources(query: string): Promise<ResourceSummary[]> {
    const platform = this.sketch().settings.platform ?? 'azure';
    this.schemaLoading.set(true);
    try {
      return await firstValueFrom(this.http.get<ResourceSummary[]>(`/api/schemas/${platform}/search`, { params: { q: query } }));
    } finally {
      this.schemaLoading.set(false);
    }
  }

  async loadSchemaStatus(): Promise<void> {
    const platform = this.sketch().settings.platform ?? 'azure';
    try {
      this.schemaStatus.set(await firstValueFrom(this.http.get<SchemaStatus>(`/api/schemas/${platform}/status`)));
    } catch {
      this.schemaStatus.set(null);
    }
  }

  // Downloads the newest provider the version range allows; the fields already read are read again from the new list.
  async refreshSchemas(): Promise<void> {
    const platform = this.sketch().settings.platform ?? 'azure';
    this.schemaRefreshing.set(true);
    try {
      this.schemaStatus.set(await firstValueFrom(this.http.post<SchemaStatus>(`/api/schemas/${platform}/refresh`, {})));
      this.schemas.set({});
    } catch {
      await this.loadSchemaStatus();
    } finally {
      this.schemaRefreshing.set(false);
    }
  }

  async loadSchema(type: string): Promise<ResourceSchema | null> {
    const known = this.schemas()[type];
    if (known) {
      return known;
    }

    const platform = this.sketch().settings.platform ?? 'azure';
    try {
      const schema = await firstValueFrom(this.http.get<ResourceSchema>(`/api/schemas/${platform}/types/${type}`));
      this.schemas.update(current => ({ ...current, [type]: schema }));
      return schema;
    } catch {
      return null;
    }
  }

  // A field of a provider box that points to another box: "ref:<box id>.<attribute>", shown as an arrow.
  setReference(nodeId: string, key: string, targetId: string, attribute: string): void {
    this.update(current => {
      const nodes = current.nodes.map(node => (node.id === nodeId ? { ...node, props: { ...node.props, [key]: `${REFERENCE_PREFIX}${targetId}.${attribute}` } } : node));
      const linked = current.edges.some(edge => edge.from === nodeId && edge.to === targetId);
      return { ...current, nodes, edges: linked ? current.edges : [...current.edges, { id: newId('e'), from: nodeId, to: targetId }] };
    });
  }

  // Clearing a field that pointed to a box removes its arrow when no other field points there.
  clearProp(nodeId: string, key: string): void {
    this.update(current => {
      const nodes = current.nodes.map(node => {
        if (node.id !== nodeId) {
          return node;
        }

        const props = { ...node.props };
        delete props[key];
        return { ...node, props };
      });
      const node = nodes.find(item => item.id === nodeId)!;
      const targets = new Set(Object.values(node.props).filter(value => value.startsWith(REFERENCE_PREFIX))
        .map(value => value.slice(REFERENCE_PREFIX.length, value.lastIndexOf('.'))));
      const edges = current.edges.filter(edge => edge.from !== nodeId || !isGeneric(node.kind) || targets.has(edge.to));
      return { ...current, nodes, edges };
    });
  }

  // AI proposals ----------------------------------------------------------------------------------------------------

  private async loadAiStatus(): Promise<void> {
    try {
      this.aiStatus.set(await firstValueFrom(this.http.get<AiStatus>('/api/ai/status')));
    } catch {
      this.aiStatus.set({ enabled: false, provider: '', model: '', problem: 'The Terraform Generator server did not answer.' });
    }
  }

  // A correction starts from the proposal on screen; the changes always compare with the drawing before the AI.
  async propose(description: string, startFresh: boolean): Promise<boolean> {
    const current = this.proposal()?.sketch ?? this.sketch();
    this.proposing.set(true);
    this.proposalError.set(null);
    try {
      const proposal = await firstValueFrom(this.http.post<SketchProposal>('/api/ai/propose', {
        description, current, startFresh: startFresh && !this.previewing(), baseline: this.sketch(),
      }));
      this.proposal.set(proposal);
      this.selection.set(null);
      return true;
    } catch (error) {
      this.proposalError.set(problemOf(error));
      return false;
    } finally {
      this.proposing.set(false);
    }
  }

  approveProposal(): void {
    const proposal = this.proposal();
    if (proposal) {
      this.proposal.set(null);
      this.replace(proposal.sketch);
      this.recommendations.set(null);
      this.approvals.update(count => count + 1);
    }
  }

  discardProposal(): void {
    this.proposal.set(null);
    this.selection.set(null);
  }

  // Existing .tf files become a proposed drawing, read by rules or by the AI.
  async importTerraform(files: File[], useAi: boolean): Promise<boolean> {
    this.proposing.set(true);
    this.proposalError.set(null);
    try {
      // A sketch.json chosen with the .tf files gives the boxes the places they had in the drawing.
      const drawing = files.find(file => file.name.toLowerCase().endsWith('.json'));
      let layout: Sketch | null = null;
      if (drawing) {
        try {
          layout = JSON.parse(await drawing.text()) as Sketch;
        } catch {
          layout = null;
        }

        if (!Array.isArray(layout?.nodes)) {
          this.proposalError.set(`${drawing.name} is not a Terraform Generator drawing.`);
          return false;
        }
      }

      const terraform = files.filter(file => file !== drawing);
      const parts = await Promise.all(terraform.map(async file => `# ---- ${file.name}\n${await file.text()}`));
      const proposal = await firstValueFrom(this.http.post<SketchProposal>('/api/import', {
        terraform: parts.join('\n'), useAi, current: this.sketch(), layout,
      }));
      this.proposal.set(proposal);
      this.selection.set(null);
      return true;
    } catch (error) {
      this.proposalError.set(problemOf(error));
      return false;
    } finally {
      this.proposing.set(false);
    }
  }

  async recommend(): Promise<void> {
    this.recommending.set(true);
    this.recommendationError.set(null);
    try {
      this.recommendations.set(await firstValueFrom(this.http.post<RecommendationResult>('/api/ai/recommend', {
        sketch: this.sketch(), language: navigator.language,
      })));
    } catch (error) {
      this.recommendationError.set(problemOf(error));
    } finally {
      this.recommending.set(false);
    }
  }

  // A recommendation's change goes through the usual proposal: nothing changes until it is approved.
  async applyRecommendation(recommendation: Recommendation): Promise<boolean> {
    const applied = await this.propose(recommendation.change, false);
    if (!applied) {
      this.recommendationError.set(this.proposalError());
    }

    return applied;
  }

  // Server ----------------------------------------------------------------------------------------------------------

  private async loadCatalog(): Promise<void> {
    try {
      this.catalog.set(await firstValueFrom(this.http.get<SketchCatalog>('/api/catalog')));
      this.serverError.set(null);
    } catch (error) {
      this.serverError.set(describe(error));
    }
  }

  private async generate(sketch: Sketch): Promise<void> {
    const generation = ++this.generation;
    this.generating.set(true);
    try {
      const result = await firstValueFrom(this.http.post<GenerationResult>('/api/generate', sketch));
      if (generation === this.generation) {
        this.result.set(result);
        this.serverError.set(null);
      }
    } catch (error) {
      if (generation === this.generation) {
        this.serverError.set(describe(error));
      }
    } finally {
      if (generation === this.generation) {
        this.generating.set(false);
      }
    }
  }

  async runCheck(): Promise<void> {
    this.checking.set(true);
    this.check.set(null);
    try {
      this.check.set(await firstValueFrom(this.http.post<TerraformCheck>('/api/validate', this.sketch())));
    } catch (error) {
      this.check.set({ available: true, passed: false, steps: [{ command: 'check', exitCode: -1, output: describe(error) }] });
    } finally {
      this.checking.set(false);
    }
  }

  async downloadZip(): Promise<void> {
    const sketch = this.sketch();
    const blob = await firstValueFrom(this.http.post('/api/export', sketch, { responseType: 'blob' }));
    saveBlob(blob, `${sketch.settings.name}-${sketch.settings.environment}-infrastructure.zip`);
  }

  downloadDrawing(): void {
    const sketch = this.sketch();
    saveBlob(new Blob([JSON.stringify(sketch, null, 2)], { type: 'application/json' }), `${sketch.settings.name}.sketch.json`);
  }

  async openDrawing(file: File): Promise<void> {
    const parsed = JSON.parse(await file.text()) as Partial<Sketch>;
    this.replace({
      settings: { ...emptySketch().settings, ...(parsed.settings ?? {}), options: parsed.settings?.options ?? {} },
      nodes: (parsed.nodes ?? []).map(node => ({ ...node, props: node.props ?? {} })),
      edges: parsed.edges ?? [],
    });
  }

  private restore(): Sketch {
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (saved) {
        const sketch = JSON.parse(saved) as Sketch;
        return { ...sketch, settings: { ...emptySketch().settings, ...sketch.settings, platform: sketch.settings?.platform ?? 'azure', options: sketch.settings?.options ?? {} } };
      }
    } catch {
      // Nothing saved or storage refused: start empty.
    }

    return emptySketch();
  }
}

// The first place on a grid, row by row, where a new box does not cover another one.
function freeSpot(nodes: SketchNode[]): { x: number; y: number } {
  for (let row = 0; ; row++) {
    for (let column = 0; column < 4; column++) {
      const x = 40 + column * 210;
      const y = 40 + row * 110;
      const taken = nodes.some(node => Math.abs(node.x - x) < NODE_WIDTH + 20 && Math.abs(node.y - y) < NODE_HEIGHT + 20);
      if (!taken) {
        return { x, y };
      }
    }
  }
}

function problemOf(error: unknown): string {
  if (error instanceof HttpErrorResponse && typeof error.error?.problem === 'string') {
    return error.error.problem;
  }

  return describe(error);
}

function describe(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    return error.status === 0 ? 'The Terraform Generator server is not running (http://localhost:5080).' : `${error.status} ${error.statusText}`;
  }

  return error instanceof Error ? error.message : String(error);
}

function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.click();
  URL.revokeObjectURL(url);
}

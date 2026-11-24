// The same shapes as TerraformGenerator.Domain on the server.
export interface SketchSettings {
  platform: string;
  name: string;
  environment: string;
  region: string;
  monitoring: boolean;
  options: Record<string, string>;
}

export interface SketchNode {
  id: string;
  kind: string;
  name: string;
  x: number;
  y: number;
  props: Record<string, string>;
}

export interface SketchEdge {
  id: string;
  from: string;
  to: string;
}

export interface Sketch {
  settings: SketchSettings;
  nodes: SketchNode[];
  edges: SketchEdge[];
}

export type MessageLevel = 'info' | 'warning' | 'error';

export interface SketchMessage {
  level: MessageLevel;
  text: string;
  nodeId?: string | null;
  edgeId?: string | null;
}

export interface GenerationResult {
  files: Record<string, string>;
  messages: SketchMessage[];
  hasErrors: boolean;
}

export interface PropertyOption {
  value: string;
  label: string;
}

export interface PropertyDefinition {
  key: string;
  label: string;
  type: 'select' | 'toggle' | 'text';
  default: string;
  options: PropertyOption[];
  pattern?: string | null;
  help?: string | null;
  group?: string | null;
  showWhen?: string | null;
}

export interface ResourceKind {
  kind: string;
  label: string;
  short: string;
  group: string;
  description: string;
  properties: PropertyDefinition[];
  platform: string;
}

export interface ConnectionDefinition {
  from: string;
  to: string;
  description: string;
}

export interface SketchCatalog {
  kinds: ResourceKind[];
  connections: ConnectionDefinition[];
  regions: PropertyOption[];
  environments: PropertyOption[];
  settings: PropertyDefinition[];
  namePattern: string;
  projectNamePattern: string;
}

export interface TerraformStep {
  command: string;
  exitCode: number;
  output: string;
}

export interface TerraformCheck {
  available: boolean;
  passed: boolean;
  steps: TerraformStep[];
}

export interface AiStatus {
  enabled: boolean;
  provider: string;
  model: string;
  problem?: string | null;
}

export interface ProposalChange {
  type: 'added' | 'changed' | 'removed';
  text: string;
  nodeId?: string | null;
}

export interface ReviewItem {
  text: string;
  nodeId?: string | null;
}

export interface SketchProposal {
  summary: string;
  sketch: Sketch;
  changes: ProposalChange[];
  review: ReviewItem[];
  messages: SketchMessage[];
}

export interface Recommendation {
  title: string;
  category: 'security' | 'reliability' | 'cost' | 'performance' | 'operations';
  priority: 'high' | 'medium' | 'low';
  why: string;
  change: string;
}

export interface RecommendationResult {
  summary: string;
  recommendations: Recommendation[];
}

export type Selection = { type: 'node'; id: string } | { type: 'edge'; id: string } | null;

export const NODE_WIDTH = 168;
export const NODE_HEIGHT = 60;

// The three clouds and every resource of their Terraform providers.
export interface CloudPlatform {
  id: string;
  label: string;
  provider: string;
  prefix: string;
  regions: PropertyOption[];
}

export interface ResourceSummary {
  type: string;
  title: string;
  short: string;
}

// The provider list of a cloud: how many resources, from which provider version, written when, and why it failed.
export interface SchemaStatus {
  platform: string;
  ready: boolean;
  loading: boolean;
  count: number;
  problem: string | null;
  version: string | null;
  updatedUtc: string | null;
}

export interface SchemaAttribute {
  name: string;
  type: string;
  required: boolean;
  optional: boolean;
  computed: boolean;
  sensitive: boolean;
  description?: string | null;
}

export interface SchemaBlock {
  name: string;
  nesting: string;
  min: number;
  max: number;
  attributes: SchemaAttribute[];
  blocks: SchemaBlock[];
  description?: string | null;
}

export interface ResourceSchema {
  type: string;
  platform: string;
  description?: string | null;
  attributes: SchemaAttribute[];
  blocks: SchemaBlock[];
}

// The palette of one cloud as the person left it: pinned provider services and renamed sections (by their usual name).
export interface PalettePreferences {
  pins: string[];
  sections: Record<string, string>;
}

// The section a pinned provider service goes in, by the words of its type; OTHERS when none fits.
export const OTHERS = 'OTHERS';
const GROUP_WORDS: [string, string[]][] = [
  ['AI', ['bedrock', 'sagemaker', 'vertex', 'cognitive', 'machine_learning', 'document_ai', 'dialogflow', 'lex', 'kendra', 'comprehend', 'rekognition',
    'textract', 'translate', 'polly', 'transcribe', 'search_service', 'bot', 'video_indexer', 'discovery_engine', 'notebooks', 'workbench', 'speech', 'vision']],
  ['Edge', ['cloudfront', 'route53', 'dns', 'cdn', 'frontdoor', 'lb', 'load_balancer', 'api_gateway', 'apigatewayv2', 'apigateway', 'application_gateway',
    'global_address', 'traffic_manager', 'forwarding_rule', 'url_map', 'backend_service', 'api_management']],
  ['Messaging', ['sqs', 'sns', 'pubsub', 'eventhub', 'eventgrid', 'servicebus', 'kinesis', 'mq', 'msk', 'kafka', 'eventbridge', 'cloudwatch_event',
    'notification', 'signalr', 'web_pubsub', 'communication', 'ses', 'sesv2', 'redis', 'elasticache', 'memorystore', 'tasks', 'scheduler']],
  ['Security', ['iam', 'kms', 'key_vault', 'secret', 'acm', 'certificate', 'waf', 'firewall', 'guardduty', 'security', 'cognito', 'identity', 'policy',
    'role', 'container_registry', 'ecr', 'artifact_registry', 'app_configuration', 'ssm']],
  ['Data', ['db', 'rds', 'sql', 'dynamodb', 'cosmosdb', 'storage', 's3', 'bucket', 'firestore', 'bigtable', 'spanner', 'bigquery', 'postgresql', 'mysql',
    'mariadb', 'mongo', 'redshift', 'glue', 'data_factory', 'synapse', 'efs', 'ebs', 'fsx', 'filestore', 'backup', 'datastream', 'dataproc', 'athena']],
  ['Apps', ['lambda', 'ecs', 'eks', 'kubernetes', 'container', 'app_service', 'web_app', 'function', 'cloud_run', 'cloudfunctions', 'compute_instance',
    'instance', 'virtual_machine', 'app_engine', 'apprunner', 'batch', 'elastic_beanstalk', 'logic_app', 'static_site', 'amplify']],
];

export function groupOf(type: string, platform: string): string {
  const bare = type.slice(type.indexOf('_') + 1);
  const words = bare.split('_');
  for (const [group, keys] of GROUP_WORDS) {
    // A word matches as it is or as the start of a longer word (bedrock in bedrockagent).
    if (keys.some(key => key.includes('_') ? bare.includes(key) : words.some(word => word === key || (key.length >= 5 && word.startsWith(key))))) {
      return group === 'Messaging' && platform === 'azure' ? 'Messaging and cache' : group;
    }
  }

  return OTHERS;
}

// The examples the server has.
export type ExampleName = 'simple' | 'everything' | 'aws' | 'gcp';

export const GENERIC_PREFIX = 'tf:';
export const EXTRA_HCL = '_hcl';
export const REFERENCE_PREFIX = 'ref:';

export function isGeneric(kind: string): boolean {
  return kind.startsWith(GENERIC_PREFIX);
}

export function typeOf(kind: string): string {
  return kind.slice(GENERIC_PREFIX.length);
}

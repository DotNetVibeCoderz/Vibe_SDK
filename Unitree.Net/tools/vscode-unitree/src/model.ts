/** One telemetry sample, as `unitree probe` and `unitree stream` emit it. */
export interface RobotTelemetry {
    batteryPercent: number;
    packVoltage: number;
    currentAmps: number;
    cycleCount: number;
    cellImbalanceMillivolts: number;
    estimatedMinutesRemaining?: number;
    maxMotorTemperatureCelsius: number;
    rollDegrees: number;
    pitchDegrees: number;
    yawDegrees: number;
    bodyHeight: number;
    speed: number;
    odometryX: number;
    odometryY: number;
    feetLoaded: number;
    isFullStance: boolean;
    isAirborne: boolean;
}

/** A connection state report. `telemetry` is absent until the first state message arrives. */
export interface RobotSnapshot {
    connected: boolean;
    state?: string;
    model: string;
    transport?: string;
    endpoint?: string;
    lowStateCount?: number;
    sportStateCount?: number;
    timestamp?: string;
    error?: string;
    telemetry?: RobotTelemetry | null;
}

/** A project template, as `unitree templates` emits it. */
export interface TemplateInfo {
    id: string;
    name: string;
    summary: string;
    kind: 'Console' | 'Desktop' | 'Web' | 'Embedded';
    tags: string[];
    files: number;
}

/** A scaffolded project, as `unitree new` emits it. */
export interface CreatedProject {
    name: string;
    rootPath: string;
    projectFilePath: string;
    kind: string;
    templateId?: string;
}

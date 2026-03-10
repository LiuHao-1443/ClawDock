import { useState } from "react";
import { open } from "@tauri-apps/plugin-shell";
import {
  Play,
  Square,
  RotateCw,
  ExternalLink,
  Terminal,
  Settings,
  Trash2,
} from "lucide-react";
import { useGatewayStore } from "../../stores/gateway-store";
import { useUiStore } from "../../stores/ui-store";
import { gatewayStart, gatewayStop, gatewayRestart } from "../../lib/tauri-commands";
import { UninstallDialog } from "../settings/UninstallDialog";
import { useT, useI18n } from "../../lib/i18n";
import { Button } from "../ui/button";
import { Separator } from "../ui/separator";

export function Toolbar() {
  const t = useT();
  const toggleLocale = useI18n((s) => s.toggleLocale);
  const status = useGatewayStore((s) => s.status);
  const dashboardUrl = useGatewayStore((s) => s.dashboardUrl);
  const { toggleConsole, toggleSettings } = useUiStore();
  const [uninstallOpen, setUninstallOpen] = useState(false);

  const isRunning = status === "Running";
  const isStarting = status === "Starting";

  return (
    <>
      <div className="h-11 bg-card border-b border-border flex items-center px-3 gap-1 shrink-0">
        {/* Logo */}
        <div className="flex items-center gap-2 mr-4">
          <img src="/logo-32.png" alt="ClawDock" className="w-7 h-7" />
          <span className="text-foreground font-semibold text-[15px]">
            ClawDock
          </span>
        </div>

        {/* Control buttons */}
        <ToolbarButton
          onClick={() => gatewayStart()}
          disabled={isRunning || isStarting}
          icon={<Play className="h-3.5 w-3.5" />}
          label={t("toolbar.start")}
        />
        <ToolbarButton
          onClick={() => gatewayStop()}
          disabled={!isRunning && !isStarting}
          icon={<Square className="h-3.5 w-3.5" />}
          label={t("toolbar.stop")}
        />
        <ToolbarButton
          onClick={() => gatewayRestart()}
          disabled={!isRunning}
          icon={<RotateCw className="h-3.5 w-3.5" />}
          label={t("toolbar.restart")}
        />

        <Separator orientation="vertical" className="mx-1 h-5" />

        <ToolbarButton
          onClick={() => {
            if (dashboardUrl) open(dashboardUrl);
          }}
          disabled={!isRunning}
          icon={<ExternalLink className="h-3.5 w-3.5" />}
          label={t("toolbar.openBrowser")}
        />

        <Separator orientation="vertical" className="mx-1 h-5" />

        <ToolbarButton
          onClick={toggleConsole}
          icon={<Terminal className="h-3.5 w-3.5" />}
          label={t("toolbar.console")}
        />

        <Separator orientation="vertical" className="mx-1 h-5" />

        <ToolbarButton
          onClick={toggleSettings}
          icon={<Settings className="h-3.5 w-3.5" />}
          label={t("toolbar.settings")}
        />

        <Separator orientation="vertical" className="mx-1 h-5" />

        <ToolbarButton
          onClick={() => setUninstallOpen(true)}
          icon={<Trash2 className="h-3.5 w-3.5" />}
          label={t("toolbar.uninstall")}
          destructive
        />

        {/* Spacer */}
        <div className="flex-1" />

        {/* Language toggle */}
        <Button
          variant="outline"
          size="sm"
          onClick={toggleLocale}
          className="mr-2 text-xs h-7"
        >
          {t("common.lang")}
        </Button>

        {/* Status dot */}
        <StatusDot status={status} />
      </div>

      <UninstallDialog open={uninstallOpen} onOpenChange={setUninstallOpen} />
    </>
  );
}

function ToolbarButton({
  onClick,
  disabled,
  icon,
  label,
  destructive,
}: {
  onClick: () => void;
  disabled?: boolean;
  icon: React.ReactNode;
  label: string;
  destructive?: boolean;
}) {
  return (
    <Button
      variant="ghost"
      size="sm"
      onClick={onClick}
      disabled={disabled}
      className={
        destructive
          ? "gap-1.5 text-muted-foreground hover:text-destructive h-8"
          : "gap-1.5 text-muted-foreground hover:text-foreground h-8"
      }
    >
      {icon}
      <span>{label}</span>
    </Button>
  );
}

function StatusDot({ status }: { status: string }) {
  const t = useT();
  const color =
    status === "Running"
      ? "bg-[hsl(var(--success))]"
      : status === "Starting"
        ? "bg-[hsl(var(--warning))] animate-pulse"
        : status === "Error"
          ? "bg-destructive"
          : "bg-muted-foreground";

  const statusMap: Record<string, Parameters<typeof t>[0]> = {
    Running: "status.running",
    Starting: "status.starting",
    Stopped: "status.stopped",
    Error: "status.error",
  };

  return (
    <div className="flex items-center gap-2">
      <div className={`w-2.5 h-2.5 rounded-full ${color}`} />
      <span className="text-xs text-muted-foreground">
        {statusMap[status] ? t(statusMap[status]) : status}
      </span>
    </div>
  );
}

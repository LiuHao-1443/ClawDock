import { useEffect, useState } from "react";
import { ArrowUpCircle } from "lucide-react";
import { useGatewayStore } from "../../stores/gateway-store";
import { useConfig } from "../../hooks/useConfig";
import { useT, type TranslationKey } from "../../lib/i18n";
import {
  openclawGetInstalledVersion,
  openclawGetLatestVersion,
} from "../../lib/tauri-commands";
import {
  Tooltip,
  TooltipContent,
  TooltipProvider,
  TooltipTrigger,
} from "../ui/tooltip";

export function StatusBar() {
  const t = useT();
  const status = useGatewayStore((s) => s.status);
  const { data: config } = useConfig();
  const [ocVersion, setOcVersion] = useState<string | null>(null);
  const [latestVersion, setLatestVersion] = useState<string | null>(null);

  const model = config?.model?.primaryModel || null;

  useEffect(() => {
    openclawGetInstalledVersion().then(setOcVersion).catch(() => {});
    openclawGetLatestVersion().then(setLatestVersion).catch(() => {});
  }, []);

  const hasUpdate = ocVersion && latestVersion && ocVersion !== latestVersion;

  const statusMap: Record<string, TranslationKey> = {
    Running: "status.running",
    Starting: "status.starting",
    Stopped: "status.stopped",
    Error: "status.error",
  };

  return (
    <TooltipProvider delayDuration={200}>
      <div className="h-6 bg-card border-t border-border flex items-center justify-between px-3 shrink-0 gap-3">
        {/* Left: version + model */}
        <div className="flex items-center gap-3 min-w-0">
          <span className="text-[11px] text-muted-foreground shrink-0">
            ClawDock v{typeof __APP_VERSION__ !== "undefined" ? __APP_VERSION__ : "dev"}
          </span>

          {ocVersion && (
            <>
              <Dot />
              <Tooltip>
                <TooltipTrigger asChild>
                  <span className="text-[11px] text-muted-foreground shrink-0 flex items-center gap-1">
                    OpenClaw v{ocVersion}
                    {hasUpdate && (
                      <ArrowUpCircle className="h-3 w-3 text-[hsl(var(--warning))]" />
                    )}
                  </span>
                </TooltipTrigger>
                <TooltipContent side="top">
                  {hasUpdate
                    ? `${t("info.updateAvailable")}: v${latestVersion}`
                    : t("info.upToDate")}
                </TooltipContent>
              </Tooltip>
            </>
          )}

          {model && (
            <>
              <Dot />
              <span className="text-[11px] text-muted-foreground truncate font-mono">
                {model}
              </span>
            </>
          )}
        </div>

        {/* Right: gateway status */}
        <span className="text-[11px] text-muted-foreground shrink-0">
          {t("status.gateway")}: {statusMap[status] ? t(statusMap[status]) : status}
        </span>
      </div>
    </TooltipProvider>
  );
}

function Dot() {
  return <span className="text-[11px] text-muted-foreground/40">·</span>;
}

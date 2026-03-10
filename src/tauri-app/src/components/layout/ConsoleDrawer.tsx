import { useEffect, useRef } from "react";
import { X } from "lucide-react";
import { useGatewayStore } from "../../stores/gateway-store";
import { useUiStore } from "../../stores/ui-store";
import { useT } from "../../lib/i18n";
import { Button } from "../ui/button";
import { ScrollArea } from "../ui/scroll-area";

export function ConsoleDrawer() {
  const t = useT();
  const logs = useGatewayStore((s) => s.logs);
  const clearLogs = useGatewayStore((s) => s.clearLogs);
  const setConsoleOpen = useUiStore((s) => s.setConsoleOpen);
  const bottomRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [logs]);

  return (
    <div className="absolute inset-0 bg-background flex flex-col z-10">
      {/* Header */}
      <div className="flex items-center justify-between px-4 py-2 border-b border-border shrink-0">
        <span className="text-sm font-medium text-foreground">{t("console.title")}</span>
        <div className="flex items-center gap-1">
          <Button
            variant="ghost"
            size="sm"
            onClick={clearLogs}
            className="text-xs text-muted-foreground hover:text-foreground h-7"
          >
            {t("console.clear")}
          </Button>
          <Button
            variant="ghost"
            size="icon"
            onClick={() => setConsoleOpen(false)}
            className="h-7 w-7 text-muted-foreground hover:text-foreground"
          >
            <X className="h-4 w-4" />
          </Button>
        </div>
      </div>

      {/* Log content */}
      <ScrollArea className="flex-1 p-3">
        <div className="font-mono text-xs leading-relaxed">
          {logs.length === 0 ? (
            <span className="text-muted-foreground">{t("console.empty")}</span>
          ) : (
            logs.map((log) => (
              <div key={log.id} className="text-muted-foreground whitespace-pre-wrap">
                <span className="text-muted-foreground/50 mr-2">
                  {new Date(log.timestamp).toLocaleTimeString()}
                </span>
                {log.message}
              </div>
            ))
          )}
          <div ref={bottomRef} />
        </div>
      </ScrollArea>
    </div>
  );
}

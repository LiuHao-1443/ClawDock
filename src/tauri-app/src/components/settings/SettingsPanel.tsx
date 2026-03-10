import { ArrowLeft, Cpu, Radio } from "lucide-react";
import { useUiStore } from "../../stores/ui-store";
import { ModelTab } from "./ModelTab";
import { ChannelTab } from "./ChannelTab";
import { useT } from "../../lib/i18n";
import { Button } from "../ui/button";
import { ScrollArea } from "../ui/scroll-area";
import { Separator } from "../ui/separator";
import { cn } from "../../lib/utils";

export function SettingsPanel() {
  const t = useT();
  const { activeSettingsTab, setActiveSettingsTab, setSettingsOpen } =
    useUiStore();

  const tabs = [
    { id: "model" as const, label: t("settings.models"), icon: Cpu },
    { id: "channel" as const, label: t("settings.channels"), icon: Radio },
  ];

  return (
    <div className="absolute inset-0 z-20 flex bg-background">
      {/* Left sidebar */}
      <div className="w-52 border-r border-border flex flex-col shrink-0 bg-card/50">
        {/* Header */}
        <div className="relative flex items-center justify-center px-4 h-14 shrink-0">
          <Button
            variant="ghost"
            size="icon"
            onClick={() => setSettingsOpen(false)}
            className="absolute left-4 h-8 w-8 rounded-lg text-muted-foreground hover:text-foreground"
          >
            <ArrowLeft className="h-4 w-4" />
          </Button>
          <h2 className="text-base font-semibold text-foreground tracking-tight">
            {t("settings.title")}
          </h2>
        </div>

        <Separator />

        {/* Nav */}
        <nav className="flex-1 px-3 py-3 space-y-1">
          {tabs.map((tab) => (
            <button
              key={tab.id}
              onClick={() => setActiveSettingsTab(tab.id)}
              className={cn(
                "w-full flex items-center justify-center gap-3 px-3 py-2.5 rounded-lg text-sm transition-all duration-150",
                activeSettingsTab === tab.id
                  ? "bg-primary/10 text-primary font-medium shadow-sm"
                  : "text-muted-foreground hover:text-foreground hover:bg-secondary/80"
              )}
            >
              <tab.icon className="h-4 w-4 shrink-0" />
              {tab.label}
            </button>
          ))}
        </nav>

      </div>

      {/* Right content area */}
      <div className="flex-1 flex flex-col overflow-hidden">
        <ScrollArea className="flex-1">
          <div className="max-w-2xl mx-auto p-8">
            {activeSettingsTab === "model" ? <ModelTab /> : <ChannelTab />}
          </div>
        </ScrollArea>
      </div>
    </div>
  );
}

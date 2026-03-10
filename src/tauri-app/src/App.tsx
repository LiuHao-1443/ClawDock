import { useEffect, useState } from "react";
import { listen } from "@tauri-apps/api/event";
import { getCurrentWindow, LogicalSize } from "@tauri-apps/api/window";
import { installStateLoad, gatewayStart, gatewayStop } from "./lib/tauri-commands";
import { useGatewayStore } from "./stores/gateway-store";
import { MainPage } from "./pages/MainPage";
import { InstallPage } from "./pages/InstallPage";
import type { GatewayStatus } from "./lib/types";

/** Shrink window for install wizard */
async function shrinkWindow() {
  try {
    const win = getCurrentWindow();
    await win.setMinSize(new LogicalSize(480, 400));
    await win.setSize(new LogicalSize(680, 520));
    await win.center();
  } catch (e) {
    console.warn("shrinkWindow failed:", e);
  }
}

/** Resize window to full main-page dimensions */
async function expandWindow() {
  try {
    const win = getCurrentWindow();
    await win.setMinSize(new LogicalSize(800, 600));
    await win.setSize(new LogicalSize(1200, 780));
    await win.center();
  } catch (e) {
    console.warn("expandWindow failed:", e);
  }
}

function App() {
  const [loading, setLoading] = useState(true);
  const [installed, setInstalled] = useState(false);
  const { setStatus, addLog } = useGatewayStore();

  useEffect(() => {
    // Check install state; expand window if already installed
    installStateLoad()
      .then(async (state) => {
        if (state.installed) {
          await expandWindow();
        } else {
          await shrinkWindow();
        }
        setInstalled(state.installed);
        setLoading(false);
      })
      .catch(async () => {
        await shrinkWindow();
        setInstalled(false);
        setLoading(false);
      });
  }, []);

  // Listen to Tauri events
  useEffect(() => {
    const unlistenLog = listen<string>("gateway-log", (event) => {
      addLog(event.payload);
    });

    const unlistenStatus = listen<GatewayStatus>("gateway-status", (event) => {
      setStatus(event.payload);
    });

    // Listen to tray actions
    const unlistenTray = listen<string>("tray-action", (event) => {
      if (event.payload === "start") gatewayStart();
      if (event.payload === "stop") gatewayStop();
    });

    return () => {
      unlistenLog.then((fn) => fn());
      unlistenStatus.then((fn) => fn());
      unlistenTray.then((fn) => fn());
    };
  }, [addLog, setStatus]);

  if (loading) {
    return (
      <div className="flex items-center justify-center h-screen bg-background">
        <div className="text-muted-foreground text-lg">Loading...</div>
      </div>
    );
  }

  if (!installed) {
    return (
      <InstallPage
        onComplete={async () => {
          await expandWindow();
          setInstalled(true);
        }}
      />
    );
  }

  return <MainPage />;
}

export default App;

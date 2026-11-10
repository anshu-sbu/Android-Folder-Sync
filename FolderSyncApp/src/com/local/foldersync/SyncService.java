package com.local.foldersync;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.os.IBinder;

import java.text.DateFormat;
import java.util.Date;

/** Keeps the phone folder in step with the PC while the app is in the background. */
public class SyncService extends Service {
    static volatile String status = "Idle";
    static volatile boolean running;
    static volatile boolean kick;

    private static final String CHANNEL = "sync";
    private Thread worker;
    private volatile boolean stop;

    static void start(Context c) {
        Intent i = new Intent(c, SyncService.class);
        c.startForegroundService(i);
    }

    static void stop(Context c) {
        c.stopService(new Intent(c, SyncService.class));
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        NotificationManager nm = getSystemService(NotificationManager.class);
        nm.createNotificationChannel(new NotificationChannel(CHANNEL, "Folder Sync Android", NotificationManager.IMPORTANCE_LOW));
        startForeground(1, notification("Starting…"), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
        running = true;
        kick = true;
        if (worker == null) {
            stop = false;
            worker = new Thread(new Runnable() { public void run() { loop(); } }, "sync");
            worker.setDaemon(true);
            worker.start();
        }
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        stop = true;
        running = false;
        status = "Stopped";
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) { return null; }

    private Notification notification(String text) {
        PendingIntent pi = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class),
                PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Builder(this, CHANNEL)
                .setContentTitle("Folder Sync Android")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.stat_notify_sync)
                .setContentIntent(pi)
                .setOngoing(true)
                .build();
    }

    private void post(String text) {
        status = text;
        getSystemService(NotificationManager.class).notify(1, notification(text));
    }

    private void loop() {
        final Syncer.Listener l = new Syncer.Listener() { public void status(String s) { post(s); } };
        String lastVersion = "";
        long lastFull = 0;
        while (!stop) {
            try {
                Prefs p = new Prefs(this);
                if (!p.paired() || p.tree() == null) {
                    post("Waiting for pairing and a phone folder");
                } else {
                    String v = Http.getString(p.url("/api/version", null), p.fp()).trim();
                    long now = System.currentTimeMillis();
                    boolean due = kick || !v.equals(lastVersion) || now - lastFull > 120000;
                    if (due) {
                        kick = false;
                        String r = Syncer.run(this, l);
                        lastVersion = v;
                        lastFull = System.currentTimeMillis();
                        post(r + " · " + DateFormat.getTimeInstance(DateFormat.SHORT).format(new Date()));
                    }
                }
            } catch (Exception e) {
                post("Waiting for the PC… (" + e.getMessage() + ")");
            }
            for (int i = 0; i < 20 && !stop && !kick; i++) {
                try { Thread.sleep(200); } catch (InterruptedException ignored) { }
            }
        }
    }
}

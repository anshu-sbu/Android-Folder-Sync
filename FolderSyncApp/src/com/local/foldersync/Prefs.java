package com.local.foldersync;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Build;

import java.net.URLEncoder;

/** Pairing info (PC address + token), chosen phone folder and options. */
final class Prefs {
    private final SharedPreferences sp;
    private final SharedPreferences synced;

    Prefs(Context c) {
        sp = c.getSharedPreferences("cfg", Context.MODE_PRIVATE);
        synced = c.getSharedPreferences("synced", Context.MODE_PRIVATE);
    }

    String host() { return sp.getString("host", ""); }
    int port() { return sp.getInt("port", 0); }
    String token() { return sp.getString("token", ""); }
    String fp() { return sp.getString("fp", ""); }
    String pcName() { return sp.getString("pc", ""); }
    String tree() { return sp.getString("tree", null); }
    boolean mirror() { return sp.getBoolean("mirror", false); }
    boolean auto() { return sp.getBoolean("auto", true); }
    boolean paired() { return !host().isEmpty() && port() > 0 && !token().isEmpty() && !fp().isEmpty(); }

    void setPairing(String host, int port, String token, String pc, String fp) {
        boolean changed = !host.equals(host()) || port != port() || !token.equals(token()) || !fp.equals(fp());
        sp.edit().putString("host", host).putInt("port", port).putString("token", token).putString("pc", pc).putString("fp", fp).apply();
        if (changed) synced.edit().clear().apply();
    }

    void setPcName(String pc) { sp.edit().putString("pc", pc).apply(); }

    void setTree(String uri) {
        sp.edit().putString("tree", uri).apply();
        synced.edit().clear().apply();
    }

    void setMirror(boolean v) { sp.edit().putBoolean("mirror", v).apply(); }
    void setAuto(boolean v) { sp.edit().putBoolean("auto", v).apply(); }

    void unpair() {
        sp.edit().remove("host").remove("port").remove("token").remove("pc").remove("fp").apply();
        synced.edit().clear().apply();
    }

    SharedPreferences syncedState() { return synced; }

    /** http://host:port/path?t=TOKEN&d=DEVICE[&extra] */
    String url(String path, String extra) {
        try {
            return "https://" + host() + ":" + port() + path + "?t=" + URLEncoder.encode(token(), "UTF-8")
                    + "&d=" + URLEncoder.encode(Build.MANUFACTURER + " " + Build.MODEL, "UTF-8")
                    + (extra == null ? "" : "&" + extra);
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }
}

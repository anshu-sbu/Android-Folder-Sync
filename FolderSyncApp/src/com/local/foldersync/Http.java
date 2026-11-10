package com.local.foldersync;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;

import javax.net.ssl.HostnameVerifier;
import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSession;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/** HTTPS to the PC. The PC uses a self-signed certificate, so we trust exactly the one whose fingerprint was in the QR code. */
final class Http {
    /** The PC answered, but not with 200. */
    static final class HttpError extends IOException {
        final int code;
        HttpError(int code, String message) { super(message); this.code = code; }
    }

    private static HttpURLConnection connect(String url, String fp, int readTimeoutMs) throws IOException {
        HttpURLConnection h = (HttpURLConnection) new URL(url).openConnection();
        if (h instanceof HttpsURLConnection) pin((HttpsURLConnection) h, fp);
        h.setConnectTimeout(8000);
        h.setReadTimeout(readTimeoutMs);
        h.setUseCaches(false);
        return h;
    }

    private static String describe(int code, String serverText) {
        if (code == 401) return "The PC rejected this phone. Scan a new QR code.";
        if (code == 403) return "Turn on Two-way sync in Folder Sync PC.";
        return serverText == null || serverText.isEmpty() ? "PC answered HTTP " + code : serverText;
    }

    static HttpURLConnection open(String url, String fp) throws IOException {
        HttpURLConnection h = connect(url, fp, 25000);
        int code = h.getResponseCode();
        if (code != 200) throw new HttpError(code, describe(code, null));
        return h;
    }

    static String getString(String url, String fp) throws IOException {
        HttpURLConnection h = open(url, fp);
        try {
            return readAll(h.getInputStream());
        } finally {
            h.disconnect();
        }
    }

    /** PUT (with a body of exactly {@code size} bytes) or DELETE. Returns the response text; any answer but 200 throws HttpError. */
    static String send(String method, String url, String fp, InputStream body, long size) throws IOException {
        HttpURLConnection h = connect(url, fp, 120000);
        try {
            h.setRequestMethod(method);
            if (body != null) {
                h.setDoOutput(true);
                h.setFixedLengthStreamingMode(size);
                OutputStream out = h.getOutputStream();
                try {
                    byte[] buf = new byte[64 * 1024];
                    int n;
                    while ((n = body.read(buf)) > 0) out.write(buf, 0, n);
                } finally {
                    out.close();
                }
            }
            int code = h.getResponseCode();
            String text = readAll(code >= 400 ? h.getErrorStream() : h.getInputStream());
            if (code != 200) throw new HttpError(code, describe(code, text));
            return text;
        } finally {
            h.disconnect();
        }
    }

    private static String readAll(InputStream in) throws IOException {
        if (in == null) return "";
        ByteArrayOutputStream bo = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = in.read(buf)) > 0) bo.write(buf, 0, n);
        return bo.toString("UTF-8");
    }

    private static void pin(HttpsURLConnection h, final String fp) throws IOException {
        try {
            TrustManager tm = new X509TrustManager() {
                public void checkClientTrusted(X509Certificate[] chain, String authType) { }

                public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
                    if (chain == null || chain.length == 0) throw new CertificateException("No certificate");
                    try {
                        byte[] d = MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded());
                        StringBuilder sb = new StringBuilder();
                        for (byte b : d) sb.append(String.format("%02x", b));
                        if (fp == null || fp.isEmpty() || !sb.toString().startsWith(fp.toLowerCase()))
                            throw new CertificateException("This isn't the PC you paired with.");
                    } catch (CertificateException e) {
                        throw e;
                    } catch (Exception e) {
                        throw new CertificateException(e);
                    }
                }

                public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
            };
            SSLContext ctx = SSLContext.getInstance("TLS");
            ctx.init(null, new TrustManager[]{tm}, null);
            h.setSSLSocketFactory(ctx.getSocketFactory());
            h.setHostnameVerifier(new HostnameVerifier() {
                public boolean verify(String hostname, SSLSession session) { return true; }   // identity is the pinned certificate
            });
        } catch (Exception e) {
            throw new IOException(e);
        }
    }
}

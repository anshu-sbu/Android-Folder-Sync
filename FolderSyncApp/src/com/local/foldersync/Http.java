package com.local.foldersync;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
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
    static HttpURLConnection open(String url, final String fp) throws IOException {
        HttpURLConnection h = (HttpURLConnection) new URL(url).openConnection();
        if (h instanceof HttpsURLConnection) pin((HttpsURLConnection) h, fp);
        h.setConnectTimeout(8000);
        h.setReadTimeout(25000);
        h.setUseCaches(false);
        int code = h.getResponseCode();
        if (code == 401 || code == 403) throw new IOException("The PC rejected this phone. Scan a new QR code.");
        if (code != 200) throw new IOException("PC answered HTTP " + code);
        return h;
    }

    static String getString(String url, String fp) throws IOException {
        HttpURLConnection h = open(url, fp);
        try {
            InputStream in = h.getInputStream();
            ByteArrayOutputStream bo = new ByteArrayOutputStream();
            byte[] buf = new byte[8192];
            int n;
            while ((n = in.read(buf)) > 0) bo.write(buf, 0, n);
            return bo.toString("UTF-8");
        } finally {
            h.disconnect();
        }
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

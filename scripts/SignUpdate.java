import java.nio.file.*;
import java.security.*;
import java.util.*;
import java.util.regex.*;

// Run by release.ps1. Signing secrets remain in the existing local keystore and process environment.
class SignUpdate {
    public static void main(String[] args) throws Exception {
        char[] password=System.getenv("SM_KEYSTORE_PASSWORD").toCharArray();
        char[] keyPassword=System.getenv("SM_KEY_PASSWORD").toCharArray();
        try {
            KeyStore store=KeyStore.getInstance("JKS");
            try(var stream=Files.newInputStream(Path.of(System.getenv("SM_KEYSTORE_PATH")))) { store.load(stream,password); }
            String alias=System.getenv("SM_KEY_ALIAS");
            String fingerprint=HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(store.getCertificate(alias).getEncoded()));
            if(!fingerprint.equals("7217017ad413a98cc79e74bf44a6d4e13c93852ca32ef538e75881db1cda7687")) throw new SecurityException("Original release certificate required");
            String publicKey=Base64.getEncoder().encodeToString(store.getCertificate(alias).getPublicKey().getEncoded());
            for(int i=1;i<args.length;i++) {
                Matcher pin=Pattern.compile("(?:PublicKey|PUBLIC_KEY)\\s*=\\s*\"([^\"]+)\"").matcher(Files.readString(Path.of(args[i])));
                if(!pin.find()||!pin.group(1).equals(publicKey))throw new SecurityException("Updater public key differs from release certificate");
            }
            Signature signer=Signature.getInstance("SHA256withRSA");
            signer.initSign((PrivateKey)store.getKey(alias,keyPassword));
            signer.update(Files.readAllBytes(Path.of(args[0])));
            Files.writeString(Path.of(args[0]+".sig"),Base64.getEncoder().encodeToString(signer.sign()));
        } finally { Arrays.fill(password,'\0');Arrays.fill(keyPassword,'\0'); }
    }
}

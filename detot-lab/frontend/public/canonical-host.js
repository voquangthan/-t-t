if (location.hostname === "127.0.0.1") {
  const canonical = new URL(location.href);
  canonical.hostname = "localhost";
  location.replace(canonical.href);
}

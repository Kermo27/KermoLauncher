pub fn mask_url(url: &str) -> String {
    if url.trim().is_empty() {
        return url.to_string();
    }

    match url::Url::parse(url) {
        Ok(uri) => mask_parsed(&uri).unwrap_or_else(|| mask_by_heuristics(url)),
        Err(_) => mask_by_heuristics(url),
    }
}

fn mask_parsed(uri: &url::Url) -> Option<String> {
    let mut segments: Vec<String> = uri.path().split('/').map(|s| s.to_string()).collect();

    let mut i = 0;
    while i < segments.len() {
        if segments[i].is_empty() {
            i += 1;
            continue;
        }

        // /s/<token>
        if segments[i].eq_ignore_ascii_case("s")
            && i + 1 < segments.len()
            && !segments[i + 1].is_empty()
        {
            segments[i + 1] = "***".into();
            i += 2;
            continue;
        }

        // /public.php/dav/files/<token>/...
        if segments[i].eq_ignore_ascii_case("files")
            && i > 0
            && segments[i - 1].eq_ignore_ascii_case("dav")
            && i + 1 < segments.len()
            && !segments[i + 1].is_empty()
        {
            segments[i + 1] = "***".into();
            i += 2;
            continue;
        }

        i += 1;
    }

    let mut rebuilt = uri.clone();
    rebuilt.set_path(&segments.join("/"));
    Some(rebuilt.to_string())
}

fn mask_by_heuristics(url: &str) -> String {
    let re = regex::Regex::new(r"(?i)(/s/|/dav/files/)([^/?#\s]+)").expect("static regex");
    re.replace_all(url, "$1***").into_owned()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn mask_redacts_share_tokens() {
        assert_eq!(
            mask_url("https://cloud.example/s/SecretToken123"),
            "https://cloud.example/s/***"
        );
        assert_eq!(
            mask_url("https://cloud.example/public.php/dav/files/SecretToken123/metadata.json"),
            "https://cloud.example/public.php/dav/files/***/metadata.json"
        );
        assert_eq!(
            mask_url("https://cloud.example/public.php/dav/files/SecretToken123/Games/cover.jpg"),
            "https://cloud.example/public.php/dav/files/***/Games/cover.jpg"
        );
    }
    #[test]
    fn mask_leaves_github_alone() {
        let url = "https://api.github.com/repos/Kermo27/KermoLauncher/releases/latest";
        assert_eq!(mask_url(url), url);
    }
}

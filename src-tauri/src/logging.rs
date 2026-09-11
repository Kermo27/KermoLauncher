use std::fs::{self, File, OpenOptions};
use std::sync::Mutex;

use tracing_subscriber::layer::SubscriberExt;
use tracing_subscriber::util::SubscriberInitExt;
use tracing_subscriber::{fmt, EnvFilter};

const MAX_LOG_BYTES: u64 = 5 * 1024 * 1024;

pub fn init() {
    let dir = kermo_core::log_directory();
    match fs::create_dir_all(&dir).and_then(|_| open_log_file(&dir)) {
        Ok(file) => {
            if !try_init(Some(file)) {
                return;
            }
        }
        Err(e) => {
            eprintln!("failed to set up log file in {}: {e}", dir.display());
            if !try_init(None) {
                return;
            }
        }
    }
    after_init();
}

fn open_log_file(dir: &std::path::Path) -> std::io::Result<File> {
    let path = dir.join("kermo-launcher.log");
    if fs::metadata(&path).is_ok_and(|meta| meta.len() > MAX_LOG_BYTES) {
        let bak = dir.join("kermo-launcher.log.bak");
        let _ = fs::remove_file(&bak);
        fs::rename(&path, &bak)?;
    }
    OpenOptions::new().create(true).append(true).open(path)
}

fn try_init(file: Option<File>) -> bool {
    let stdout = fmt::layer()
        .with_ansi(cfg!(debug_assertions))
        .with_target(true);
    let filter = EnvFilter::try_from_default_env().unwrap_or_else(|_| {
        EnvFilter::new(
            "info,hyper=warn,hyper_util=warn,reqwest=warn,h2=warn,rustls=warn,tao=warn,wry=warn",
        )
    });

    let result = if let Some(file) = file {
        tracing_subscriber::registry()
            .with(filter)
            .with(stdout)
            .with(
                fmt::layer()
                    .with_ansi(false)
                    .with_target(true)
                    .with_writer(Mutex::new(file)),
            )
            .try_init()
    } else {
        tracing_subscriber::registry()
            .with(filter)
            .with(stdout)
            .try_init()
    };
    if let Err(e) = result {
        eprintln!("failed to initialize logger: {e}");
        false
    } else {
        true
    }
}

fn after_init() {
    let _ = tracing_log::LogTracer::init();
    let previous = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        tracing::error!("panic: {info}");
        previous(info);
    }));
    tracing::info!(
        version = kermo_core::app_version(),
        os = std::env::consts::OS,
        arch = std::env::consts::ARCH,
        "KermoLauncher starting"
    );
}

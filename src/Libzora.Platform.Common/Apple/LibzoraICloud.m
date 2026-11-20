#import <Foundation/Foundation.h>
#include <errno.h>
#include <stdlib.h>
#include <string.h>

#define LZ_EXPORT __attribute__((visibility("default")))
// Strings crossing the ABI are malloc-owned UTF-8, freed by lz_icloud_free.
// Operation results: 0 = success/ready, 1 = missing, 2 = error, 3 = downloading.
typedef int (*LZAccessor)(void *context, const char *coordinatedPath);

static void LZError(char **output, NSString *message)
{
    if (output) *output = strdup((message ?: @"The iCloud operation failed.").UTF8String);
}

static void LZNSError(char **output, NSError *error)
{
    LZError(output, [NSString stringWithFormat:@"%@ (%@:%ld)",
        error.localizedDescription ?: @"The iCloud operation failed.",
        error.domain ?: @"unknown", (long)error.code]);
}

static BOOL LZMissing(NSError *error)
{
    if (!error) return NO;
    if ([error.domain isEqualToString:NSCocoaErrorDomain] &&
        (error.code == NSFileNoSuchFileError || error.code == NSFileReadNoSuchFileError)) return YES;
    if ([error.domain isEqualToString:NSPOSIXErrorDomain] && error.code == ENOENT) return YES;
    NSError *underlying = error.userInfo[NSUnderlyingErrorKey];
    return underlying && underlying != error && LZMissing(underlying);
}

// Never hand a .name.icloud metadata file to the managed content accessor.
static NSURL *LZLogicalURL(const char *path)
{
    NSString *value = path ? [NSString stringWithUTF8String:path] : nil;
    if (!value || !value.isAbsolutePath)
        @throw [NSException exceptionWithName:NSInvalidArgumentException reason:@"An absolute iCloud path is required." userInfo:nil];
    NSString *name = value.lastPathComponent;
    if ([name hasPrefix:@"."] && [name hasSuffix:@".icloud"] && name.length > 8) {
        NSString *logicalName = [name substringWithRange:NSMakeRange(1, name.length - 8)];
        value = [value.stringByDeletingLastPathComponent stringByAppendingPathComponent:logicalName];
    }
    return [NSURL fileURLWithPath:value];
}

static NSURL *LZPlaceholderURL(NSURL *logical)
{
    NSString *name = [NSString stringWithFormat:@".%@.icloud", logical.lastPathComponent];
    return [[logical URLByDeletingLastPathComponent] URLByAppendingPathComponent:name];
}

static BOOL LZReachable(NSURL *url, NSError **error)
{
    NSError *failure = nil;
    BOOL exists = [url checkResourceIsReachableAndReturnError:&failure];
    if (!exists && failure && !LZMissing(failure) && error) *error = failure;
    return exists;
}

LZ_EXPORT void lz_icloud_free(void *value) { free(value); }

LZ_EXPORT char *lz_icloud_account_archive(char **error)
{
    if (error) *error = NULL;
    @autoreleasepool { @try {
        id token = NSFileManager.defaultManager.ubiquityIdentityToken;
        if (!token) return NULL;
        NSError *failure = nil;
        NSData *data = [NSKeyedArchiver archivedDataWithRootObject:token requiringSecureCoding:NO error:&failure];
        if (!data) { LZNSError(error, failure); return NULL; }
        return strdup([data base64EncodedStringWithOptions:0].UTF8String);
    } @catch (NSException *exception) { LZError(error, exception.reason); return NULL; } }
}

LZ_EXPORT char *lz_icloud_documents(const char *identifier, char **error)
{
    if (error) *error = NULL;
    @autoreleasepool { @try {
        NSFileManager *manager = NSFileManager.defaultManager;
        id token = manager.ubiquityIdentityToken;
        if (!token) { LZError(error, @"No iCloud account is signed in."); return NULL; }
        NSString *containerID = identifier ? [NSString stringWithUTF8String:identifier] : nil;
        if (![containerID isEqualToString:@"iCloud.com.kingstacker.libzora"]) {
            LZError(error, @"The iCloud container identifier is invalid."); return NULL;
        }
        NSURL *container = [manager URLForUbiquityContainerIdentifier:containerID];
        if (!container) { LZError(error, @"The entitled iCloud container is unavailable."); return NULL; }
        if (![token isEqual:manager.ubiquityIdentityToken]) {
            LZError(error, @"The iCloud account changed while resolving its container."); return NULL;
        }
        NSURL *documents = [container URLByAppendingPathComponent:@"Documents" isDirectory:YES];
        NSError *failure = nil;
        if (![manager createDirectoryAtURL:documents withIntermediateDirectories:YES attributes:nil error:&failure]) {
            LZNSError(error, failure); return NULL;
        }
        if (![token isEqual:manager.ubiquityIdentityToken]) {
            LZError(error, @"The iCloud account changed while resolving its container."); return NULL;
        }
        return strdup(documents.path.UTF8String);
    } @catch (NSException *exception) { LZError(error, exception.reason); return NULL; } }
}

// Existence: 1 = present (including placeholders), 0 = missing, -1 = error.
LZ_EXPORT int lz_icloud_item_exists(const char *path, char **error)
{
    if (error) *error = NULL;
    @autoreleasepool { @try {
        NSURL *logical = LZLogicalURL(path);
        NSError *failure = nil;
        if (LZReachable(logical, &failure)) return 1;
        if (failure) { LZNSError(error, failure); return -1; }
        if (LZReachable(LZPlaceholderURL(logical), &failure)) return 1;
        if (failure) { LZNSError(error, failure); return -1; }
        return 0;
    } @catch (NSException *exception) { LZError(error, exception.reason); return -1; } }
}

LZ_EXPORT int lz_icloud_download_status(const char *path, int requestDownload, char **error)
{
    if (error) *error = NULL;
    @autoreleasepool { @try {
        NSURL *logical = LZLogicalURL(path);
        NSURL *placeholder = LZPlaceholderURL(logical);
        NSError *failure = nil;
        BOOL hasContents = LZReachable(logical, &failure);
        if (failure) { LZNSError(error, failure); return 2; }
        BOOL hasPlaceholder = LZReachable(placeholder, &failure);
        if (failure) { LZNSError(error, failure); return 2; }
        if (!hasContents && !hasPlaceholder) return 1;

        NSArray *keys = @[NSURLIsUbiquitousItemKey, NSURLUbiquitousItemDownloadingStatusKey,
                          NSURLUbiquitousItemDownloadingErrorKey];
        NSURL *metadataURL = logical;
        NSDictionary *values = [logical resourceValuesForKeys:keys error:&failure];
        if (!values && LZMissing(failure) && hasPlaceholder) {
            failure = nil;
            metadataURL = placeholder;
            values = [placeholder resourceValuesForKeys:keys error:&failure];
        }
        if (!values) { LZNSError(error, failure); return LZMissing(failure) ? 1 : 2; }
        id ubiquitous = values[NSURLIsUbiquitousItemKey];
        BOOL isUbiquitous = [ubiquitous isKindOfClass:NSNumber.class] && [ubiquitous boolValue];
        if (requestDownload && (isUbiquitous || hasPlaceholder)) {
            // Prefer the logical document URL; the hidden metadata URL is a
            // compatibility fallback only for older placeholder implementations.
            BOOL requested = [NSFileManager.defaultManager startDownloadingUbiquitousItemAtURL:logical error:&failure];
            if (!requested && metadataURL != logical && LZMissing(failure)) {
                failure = nil;
                requested = [NSFileManager.defaultManager startDownloadingUbiquitousItemAtURL:metadataURL error:&failure];
            }
            if (!requested) {
                if (LZMissing(failure) && !LZReachable(logical, NULL) && !LZReachable(placeholder, NULL)) return 1;
                LZNSError(error, failure); return 2;
            }
            // Re-resolve fresh URLs on the next poll. Materialization can remove
            // the placeholder immediately, and these values predate the request.
            return 3;
        }
        id downloadError = values[NSURLUbiquitousItemDownloadingErrorKey];
        if ([downloadError isKindOfClass:NSError.class]) {
            LZNSError(error, downloadError); return LZMissing(downloadError) ? 1 : 2;
        }
        // A newly written local item within the real container needs no download.
        if (!isUbiquitous && hasContents && !hasPlaceholder) return 0;
        id status = values[NSURLUbiquitousItemDownloadingStatusKey];
        // Require Current (not stale Downloaded) so a device never reads an
        // outdated cached snapshot when another device has published an update.
        if (hasContents && [status isEqual:NSURLUbiquitousItemDownloadingStatusCurrent]) return 0;
        return 3;
    } @catch (NSException *exception) { LZError(error, exception.reason); return 2; } }
}

LZ_EXPORT int lz_icloud_upload_status(const char *path, char **error)
{
    if (error) *error = NULL;
    @autoreleasepool { @try {
        NSURL *logical = LZLogicalURL(path);
        NSURL *placeholder = LZPlaceholderURL(logical);
        NSError *failure = nil;
        BOOL hasContents = LZReachable(logical, &failure);
        if (failure) { LZNSError(error, failure); return 2; }
        BOOL hasPlaceholder = LZReachable(placeholder, &failure);
        if (failure) { LZNSError(error, failure); return 2; }
        if (!hasContents && !hasPlaceholder) return 1;
        NSArray *keys = @[NSURLIsUbiquitousItemKey, NSURLUbiquitousItemIsUploadedKey,
                          NSURLUbiquitousItemUploadingErrorKey];
        NSDictionary *values = [logical resourceValuesForKeys:keys error:&failure];
        if (!values && LZMissing(failure) && hasPlaceholder) {
            failure = nil;
            values = [placeholder resourceValuesForKeys:keys error:&failure];
        }
        if (!values) { LZNSError(error, failure); return LZMissing(failure) ? 1 : 2; }
        id uploadError = values[NSURLUbiquitousItemUploadingErrorKey];
        if ([uploadError isKindOfClass:NSError.class]) {
            LZNSError(error, uploadError); return 2;
        }
        id ubiquitous = values[NSURLIsUbiquitousItemKey];
        BOOL isUbiquitous = [ubiquitous isKindOfClass:NSNumber.class] && [ubiquitous boolValue];
        id uploaded = values[NSURLUbiquitousItemIsUploadedKey];
        if (isUbiquitous && [uploaded isKindOfClass:NSNumber.class] && [uploaded boolValue]) return 0;
        // A dataless placeholder represents a copy already held by iCloud.
        if (isUbiquitous && !hasContents && hasPlaceholder) return 0;
        // New local files may not be adopted by the iCloud daemon immediately.
        // Never acknowledge an ordinary local write as a remote upload.
        return 3;
    } @catch (NSException *exception) { LZError(error, exception.reason); return 2; } }
}

LZ_EXPORT void *lz_icloud_coordinator_create(char **error)
{
    if (error) *error = NULL;
    @autoreleasepool { @try {
        return (__bridge_retained void *)[[NSFileCoordinator alloc] initWithFilePresenter:nil];
    } @catch (NSException *exception) { LZError(error, exception.reason); return NULL; } }
}

LZ_EXPORT void lz_icloud_coordinator_cancel(void *handle)
{
    @autoreleasepool { @try {
        if (handle) [(__bridge NSFileCoordinator *)handle cancel];
    } @catch (NSException *exception) { /* Never unwind across the ABI. */ } }
}

LZ_EXPORT void lz_icloud_coordinator_release(void *handle)
{
    @autoreleasepool { @try {
        if (handle) { __unused NSFileCoordinator *coordinator = (__bridge_transfer NSFileCoordinator *)handle; }
    } @catch (NSException *exception) { /* Never unwind across the ABI. */ } }
}

LZ_EXPORT int lz_icloud_coordinate(void *handle, const char *path, int access,
                                  LZAccessor callback, void *context, char **error)
{
    if (error) *error = NULL;
    @autoreleasepool { @try {
        if (!handle || !callback || access < 0 || access > 2) {
            LZError(error, @"Invalid file coordination arguments."); return 2;
        }
        NSFileCoordinator *coordinator = (__bridge NSFileCoordinator *)handle;
        NSURL *logical = LZLogicalURL(path);
        NSError *failure = nil;
        __block BOOL called = NO;
        __block int callbackResult = 0;
        void (^accessor)(NSURL *) = ^(NSURL *relocatedURL) {
            called = YES;
            NSURL *relocatedLogical = LZLogicalURL(relocatedURL.path.UTF8String);
            callbackResult = callback(context, relocatedLogical.path.UTF8String);
        };
        if (access == 0) {
            [coordinator coordinateReadingItemAtURL:logical options:0 error:&failure byAccessor:accessor];
        } else {
            NSFileCoordinatorWritingOptions options = access == 2 ? NSFileCoordinatorWritingForDeleting : NSFileCoordinatorWritingForReplacing;
            [coordinator coordinateWritingItemAtURL:logical options:options error:&failure byAccessor:accessor];
        }
        if (failure) { LZNSError(error, failure); return LZMissing(failure) ? 1 : 2; }
        if (!called) { LZError(error, @"The file coordinator did not invoke its accessor."); return 2; }
        if (callbackResult) { LZError(error, @"The managed file accessor failed."); return 2; }
        return 0;
    } @catch (NSException *exception) { LZError(error, exception.reason); return 2; } }
}

LZ_EXPORT int lz_icloud_delete(const char *path, char **error)
{
    if (error) *error = NULL;
    @autoreleasepool { @try {
        NSURL *logical = LZLogicalURL(path);
        NSError *failure = nil;
        if ([NSFileManager.defaultManager removeItemAtURL:logical error:&failure]) return 0;
        if (!LZMissing(failure)) { LZNSError(error, failure); return 2; }
        // Older iCloud implementations expose only the .name.icloud URL.
        failure = nil;
        if ([NSFileManager.defaultManager removeItemAtURL:LZPlaceholderURL(logical) error:&failure] || LZMissing(failure)) return 0;
        LZNSError(error, failure); return 2;
    } @catch (NSException *exception) { LZError(error, exception.reason); return 2; } }
}

// Triggers a brief NSMetadataQuery gather over the ubiquity Documents scope so
// newly installed devices materialize remote .icloud metadata entries before listing.
LZ_EXPORT int lz_icloud_refresh_metadata(double timeoutSeconds, char **error)
{
    if (error) *error = NULL;
    @autoreleasepool { @try {
        if (!NSFileManager.defaultManager.ubiquityIdentityToken) return 0;
        double clamped = timeoutSeconds < 0.2 ? 0.2 : (timeoutSeconds > 5.0 ? 5.0 : timeoutSeconds);
        __block BOOL finished = NO;
        NSMetadataQuery *query = [[NSMetadataQuery alloc] init];
        query.searchScopes = @[NSMetadataQueryUbiquitousDocumentsScope];
        query.predicate = [NSPredicate predicateWithFormat:@"%K LIKE '*'", NSMetadataItemFSNameKey];
        NSOperationQueue *queue = [[NSOperationQueue alloc] init];
        queue.maxConcurrentOperationCount = 1;
        query.operationQueue = queue;

        dispatch_semaphore_t signal = dispatch_semaphore_create(0);
        id observer = [NSNotificationCenter.defaultCenter
            addObserverForName:NSMetadataQueryDidFinishGatheringNotification
                        object:query
                         queue:queue
                    usingBlock:^(__unused NSNotification *note) {
                        finished = YES;
                        dispatch_semaphore_signal(signal);
                    }];
        [queue addOperationWithBlock:^{
            if (![query startQuery]) {
                dispatch_semaphore_signal(signal);
            }
        }];
        dispatch_semaphore_wait(signal, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(clamped * NSEC_PER_SEC)));
        [NSNotificationCenter.defaultCenter removeObserver:observer];
        [queue addOperationWithBlock:^{
            [query stopQuery];
        }];
        [queue waitUntilAllOperationsAreFinished];
        return finished ? 0 : 0;
    } @catch (NSException *exception) { LZError(error, exception.reason); return 2; } }
}

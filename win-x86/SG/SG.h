#ifndef _SG_H_20251117
#define _SG_H_20251117
#include <Windows.h>

#ifdef __cplusplus
extern "C" {
#endif
    /**************************************************************************/
    //   函数名称：login
    //   函数功能：用ukey认证登录
    //   函数参数：
    //   [IN] id 标识
    //   [OUT] error 失败时返回的错误码，需要外部开空间（至少32字节）
    //   返回值：1:成功；0：失败
    //   备注：
    /**************************************************************************/
    BOOL login(int id ,char *error);

#ifdef __cplusplus
}
#endif

#endif
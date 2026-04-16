#include "SG.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main()
{
	BOOL ret = 0;
	char error[32] = {0};

	ret = login(0, error);
	if (0 == ret)
	{
		printf("id(0)测试失败!\n");
	}
	else
	{
		printf("id(0)测试成功!\n");
	}

	ret = login(1, error);
	if (0 == ret)
	{
		printf("id(1)测试失败!\n");
	}
	else
	{
		printf("id(1)测试成功!\n");
	}

	//ret = login(2, error);
	//if (0 == ret)
	//{
	//	printf("id(2)测试失败!\n");
	//}
	//else
	//{
	//	printf("id(2)测试成功!\n");
	//}

	ret = login(3, error);
	if (0 == ret)
	{
		printf("id(3)测试失败!\n");
	}
	else
	{
		printf("id(3)测试成功!\n");
	}

	ret = login(4, error);
	if (0 == ret)
	{
		printf("id(4)测试失败!\n");
	}
	else
	{
		printf("id(4)测试成功!\n");
	}

	system("pause");
	return 0;
}
